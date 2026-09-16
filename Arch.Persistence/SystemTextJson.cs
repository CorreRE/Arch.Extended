using System.Buffers;
using System.Text.Json;

#pragma warning disable CS1591

namespace Arch.Persistence;

public interface IJsonFormatter
{
    Type Type { get; }
    void SerializeObject(ref JsonWriter writer, object value, IJsonFormatterResolver formatterResolver);
    object DeserializeObject(ref JsonReader reader, IJsonFormatterResolver formatterResolver);
}

public interface IJsonFormatter<T> : IJsonFormatter
{
    Type IJsonFormatter.Type => typeof(T);
    void Serialize(ref JsonWriter writer, T value, IJsonFormatterResolver formatterResolver);
    T Deserialize(ref JsonReader reader, IJsonFormatterResolver formatterResolver);

    void IJsonFormatter.SerializeObject(ref JsonWriter writer, object value, IJsonFormatterResolver formatterResolver) => Serialize(ref writer, (T)value, formatterResolver);
    object IJsonFormatter.DeserializeObject(ref JsonReader reader, IJsonFormatterResolver formatterResolver) => Deserialize(ref reader, formatterResolver)!;
}

public interface IJsonFormatterResolver
{
    IJsonFormatter<T>? GetFormatter<T>();
    IJsonFormatter? GetFormatter(Type type);
}

public sealed class JsonWriter : IDisposable
{
    private readonly Utf8JsonWriter _writer;

    public JsonWriter(IBufferWriter<byte> bufferWriter) => _writer = new Utf8JsonWriter(bufferWriter);
    public JsonWriter(Stream stream) => _writer = new Utf8JsonWriter(stream);
    internal Utf8JsonWriter Writer => _writer;

    public void WriteBeginObject() => _writer.WriteStartObject();
    public void WriteEndObject() => _writer.WriteEndObject();
    public void WriteBeginArray() => _writer.WriteStartArray();
    public void WriteEndArray() => _writer.WriteEndArray();
    public void WritePropertyName(string name) => _writer.WritePropertyName(name);
    public void WriteInt32(int value) => _writer.WriteNumberValue(value);
    public void WriteUInt16(ushort value) => _writer.WriteNumberValue(value);
    public void WriteUInt32(uint value) => _writer.WriteNumberValue(value);
    public void WriteString(string? value) => _writer.WriteStringValue(value);
    public void WriteValueSeparator() { }
    public void AdvanceOffset(int offset) { }
    public void Flush() => _writer.Flush();
    public void Dispose() => _writer.Dispose();
}

public ref struct JsonReader
{
    private Utf8JsonReader _reader;
    private bool _needsOuterObjectEnd;

    public JsonReader(ReadOnlySpan<byte> json)
    {
        _reader = new Utf8JsonReader(json, true, default);
        _needsOuterObjectEnd = false;
    }

    internal T Deserialize<T>(JsonSerializerOptions options) => System.Text.Json.JsonSerializer.Deserialize<T>(ref _reader, options)!;
    internal object? Deserialize(Type type, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref _reader);
        _needsOuterObjectEnd = true;

        if (type.IsValueType && document.RootElement.ValueKind == JsonValueKind.Object)
        {
            var value = Activator.CreateInstance(type)!;
            foreach (var field in type.GetFields())
            {
                if (document.RootElement.TryGetProperty(field.Name, out var property))
                {
                    field.SetValue(value, System.Text.Json.JsonSerializer.Deserialize(property.GetRawText(), field.FieldType, options));
                }
            }

            return value;
        }

        return System.Text.Json.JsonSerializer.Deserialize(document.RootElement.GetRawText(), type, options);
    }

    public void ReadIsBeginObject() => ReadIfNeeded(JsonTokenType.StartObject);
    public void ReadIsEndObject()
    {
        if (_needsOuterObjectEnd)
        {
            _needsOuterObjectEnd = false;
            if (!_reader.Read() || _reader.TokenType != JsonTokenType.EndObject)
            {
                throw new JsonException($"Expected {JsonTokenType.EndObject}, got {_reader.TokenType}.");
            }
            return;
        }

        if (_reader.TokenType != JsonTokenType.EndObject && (!_reader.Read() || _reader.TokenType != JsonTokenType.EndObject))
        {
            throw new JsonException($"Expected {JsonTokenType.EndObject}, got {_reader.TokenType}.");
        }
    }
    public void ReadIsBeginArray()
    {
        ReadIfNeeded(JsonTokenType.StartArray);
        _reader.Read();
    }
    public void ReadIsEndArray() => ReadIfNeeded(JsonTokenType.EndArray);

    /// <summary>
    ///     Returns whether the reader is currently positioned at the end of an object.
    ///     Does not consume the current token.
    /// </summary>
    /// <returns>True if the current token is the end of an object, otherwise false.</returns>
    public bool IsEndObject() => _reader.TokenType == JsonTokenType.EndObject;

    public bool ReadIsEndArrayWithSkipValueSeparator(ref int count)
    {
        if (_reader.TokenType == JsonTokenType.EndArray)
        {
            return true;
        }

        if (count > 0)
        {
            _reader.Read();
            if (_reader.TokenType == JsonTokenType.EndArray)
            {
                return true;
            }
        }

        count++;
        return false;
    }

    public void ReadPropertyName()
    {
        ReadIfNeeded(JsonTokenType.PropertyName);
        _reader.Read();
    }

    public void ReadIsValueSeparator()
    {
        _needsOuterObjectEnd = false;
        _reader.Read();
    }

    public int ReadInt32() => _reader.GetInt32();
    public ushort ReadUInt16() => _reader.GetUInt16();
    public uint ReadUInt32() => _reader.GetUInt32();
    public string? ReadString() => _reader.GetString();

    private void ReadIfNeeded(JsonTokenType tokenType)
    {
        if (_reader.TokenType != tokenType && (!_reader.Read() || _reader.TokenType != tokenType))
        {
            throw new JsonException($"Expected {tokenType}, got {_reader.TokenType}.");
        }
    }
}

public sealed class CompositeResolver : IJsonFormatterResolver
{
    private readonly Dictionary<Type, IJsonFormatter> _formatters;

    private CompositeResolver(IEnumerable<IJsonFormatter> formatters) => _formatters = formatters.ToDictionary(formatter => formatter.Type);

    public static IJsonFormatterResolver Create(IJsonFormatter[] formatters, IJsonFormatterResolver[] resolvers)
    {
        var allFormatters = formatters.Concat(resolvers.SelectMany(GetFormatters));
        return new CompositeResolver(allFormatters);
    }

    private static IEnumerable<IJsonFormatter> GetFormatters(IJsonFormatterResolver resolver)
    {
        return resolver is CompositeResolver composite ? composite._formatters.Values : Array.Empty<IJsonFormatter>();
    }

    public IJsonFormatter<T>? GetFormatter<T>() => GetFormatter(typeof(T)) as IJsonFormatter<T>;
    public IJsonFormatter? GetFormatter(Type type)
    {
        return _formatters.GetValueOrDefault(type) ?? (type.IsArray ? _formatters.GetValueOrDefault(typeof(Array)) : null);
    }
}

internal sealed class EmptyResolver : IJsonFormatterResolver
{
    public IJsonFormatter<T>? GetFormatter<T>() => null;
    public IJsonFormatter? GetFormatter(Type type) => null;
}

public static class BuiltinResolver
{
    public static IJsonFormatterResolver Instance { get; } = new EmptyResolver();
}

public static class DynamicGenericResolver
{
    public static IJsonFormatterResolver Instance { get; } = new EmptyResolver();
}

public static class EnumResolver
{
    public static IJsonFormatterResolver UnderlyingValue { get; } = new EmptyResolver();
}

public static class StandardResolver
{
    public static IJsonFormatterResolver AllowPrivateExcludeNullSnakeCase { get; } = new EmptyResolver();
}

public sealed class DateTimeFormatter : IJsonFormatter<DateTime>
{
    private readonly string _format;
    public DateTimeFormatter(string format) => _format = format;
    public Type Type => typeof(DateTime);
    public void Serialize(ref JsonWriter writer, DateTime value, IJsonFormatterResolver formatterResolver) => writer.WriteString(value.ToString(_format));
    public DateTime Deserialize(ref JsonReader reader, IJsonFormatterResolver formatterResolver) => DateTime.Parse(reader.ReadString()!);
}

public sealed class NullableDateTimeFormatter : IJsonFormatter<DateTime?>
{
    private readonly string _format;
    public NullableDateTimeFormatter(string format) => _format = format;
    public Type Type => typeof(DateTime?);
    public void Serialize(ref JsonWriter writer, DateTime? value, IJsonFormatterResolver formatterResolver) => writer.WriteString(value?.ToString(_format));
    public DateTime? Deserialize(ref JsonReader reader, IJsonFormatterResolver formatterResolver) => DateTime.Parse(reader.ReadString()!);
}

public static class JsonSerializer
{
    public static string ToJsonString<T>(T value, IJsonFormatterResolver resolver) => System.Text.Encoding.UTF8.GetString(Serialize(value, resolver));

    public static byte[] Serialize<T>(T value, IJsonFormatterResolver resolver)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new JsonWriter(buffer);
        Serialize(writer, value, resolver);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    public static void Serialize<T>(Stream stream, T value, IJsonFormatterResolver resolver)
    {
        using var writer = new JsonWriter(stream);
        Serialize(writer, value, resolver);
        writer.Flush();
    }

    public static void Serialize<T>(IBufferWriter<byte> buffer, T value, IJsonFormatterResolver resolver)
    {
        using var writer = new JsonWriter(buffer);
        Serialize(writer, value, resolver);
        writer.Flush();
    }

    public static void Serialize<T>(ref JsonWriter writer, T value, IJsonFormatterResolver? resolver = null)
    {
        if (value is Type type)
        {
            writer.WriteString(type.AssemblyQualifiedName);
            return;
        }

        var formatter = resolver?.GetFormatter<T>();
        if (formatter is not null)
        {
            formatter.Serialize(ref writer, value!, resolver!);
            return;
        }

        var objectFormatter = resolver?.GetFormatter(typeof(T));
        if (objectFormatter is not null)
        {
            objectFormatter.SerializeObject(ref writer, value!, resolver!);
            return;
        }

        System.Text.Json.JsonSerializer.Serialize(writer.Writer, value, JsonOptions);
    }

    public static void Serialize<T>(JsonWriter writer, T value, IJsonFormatterResolver resolver) => Serialize(ref writer, value, resolver);

    public static T Deserialize<T>(string json, IJsonFormatterResolver resolver) => Deserialize<T>(System.Text.Encoding.UTF8.GetBytes(json), resolver);

    public static T Deserialize<T>(byte[] json, IJsonFormatterResolver resolver)
    {
        var reader = new JsonReader(json);
        return Deserialize<T>(ref reader, resolver);
    }

    public static T Deserialize<T>(Stream stream, IJsonFormatterResolver resolver)
    {
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return Deserialize<T>(memory.ToArray(), resolver);
    }

    public static T Deserialize<T>(ref JsonReader reader, IJsonFormatterResolver? resolver = null)
    {
        if (typeof(T) == typeof(Type))
        {
            return (T)(object)TypeResolver.Resolve(reader.ReadString())!;
        }

        var formatter = resolver?.GetFormatter<T>();
        if (formatter is not null)
        {
            return formatter.Deserialize(ref reader, resolver!);
        }

        var objectFormatter = resolver?.GetFormatter(typeof(T));
        if (objectFormatter is not null)
        {
            return (T)objectFormatter.DeserializeObject(ref reader, resolver!);
        }

        return reader.Deserialize<T>(JsonOptions);
    }

    public static class NonGeneric
    {
        public static void Serialize(ref JsonWriter writer, object? value, IJsonFormatterResolver resolver)
        {
            if (value is null)
            {
                writer.Writer.WriteNullValue();
                return;
            }

            var formatter = resolver.GetFormatter(value.GetType());
            if (formatter is not null)
            {
                formatter.SerializeObject(ref writer, value, resolver);
                return;
            }

            System.Text.Json.JsonSerializer.Serialize(writer.Writer, value, value.GetType(), JsonOptions);
        }

        public static object? Deserialize(Type type, ref JsonReader reader, IJsonFormatterResolver resolver)
        {
            if (type == typeof(Type))
            {
                return TypeResolver.Resolve(reader.ReadString());
            }

            var formatter = resolver.GetFormatter(type);
            if (formatter is not null)
            {
                return formatter.DeserializeObject(ref reader, resolver);
            }

            return reader.Deserialize(type, JsonOptions);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        IncludeFields = true
    };
}

#pragma warning restore CS1591