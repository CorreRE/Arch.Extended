using System.Text;
using System.Text.Json.Nodes;
using Arch.Core;
using Arch.Core.Extensions;
using static NUnit.Framework.Assert;

namespace Arch.Persistence.Tests;

/// <summary>
///     Checks that a persisted <see cref="World"/> is restored independently of the component ids of the current process.
/// </summary>
/// <remarks>
///     Component ids are assigned in the order in which component types are first registered during runtime and therefore
///     differ between processes. Persisted data must hence be restored by its component type names instead of its ids.
/// </remarks>
[TestFixture]
public sealed class ComponentIdTest
{
    //世界大小
    private const int m_WorldSize = 100;
    //模拟未知组件Id的偏移量
    private const int m_UnknownIdOffset = 1000;

    private readonly ArchJsonSerializer _jsonSerializer = new();

    /// <summary>
    ///     Restores a world whose persisted component ids belong to a process that registered them in another order.
    /// </summary>
    [Test]
    public void JsonWorldSerializationWithReorderedComponentIds()
    {
        var world = CreateWorld();
        var json = _jsonSerializer.ToJson(world);

        // Rotate the ids so that every component receives the id of its neighbour.
        var newWorld = _jsonSerializer.FromJson(RemapComponentIds(json, (ids, index) => ids[(index + 1) % ids.Length]));

        AssertWorld(world, newWorld);
    }

    /// <summary>
    ///     Restores a world whose persisted component ids are unknown to the current process.
    ///     <remarks>Those ids used to end up in a <see cref="ComponentType"/> that resolves to a null <see cref="Type"/>.</remarks>
    /// </summary>
    [Test]
    public void JsonWorldSerializationWithUnknownComponentIds()
    {
        var world = CreateWorld();
        var json = _jsonSerializer.ToJson(world);

        var newWorld = _jsonSerializer.FromJson(RemapComponentIds(json, (ids, index) => ids[index] + m_UnknownIdOffset));

        AssertWorld(world, newWorld);
    }

    /// <summary>
    ///     Creates a world with a variety of archetypes.
    /// </summary>
    /// <returns>The created world.</returns>
    private static World CreateWorld()
    {
        var world = World.Create();
        for (var index = 0; index < m_WorldSize; index++)
        {
            world.Create(new Transform { X = index, Y = index * 2 }, new MetaData { Name = index.ToString() });
        }

        // A second archetype with another component structure.
        for (var index = 0; index < m_WorldSize; index++)
        {
            world.Create(new Transform { X = -index, Y = -index * 2 });
        }

        // And a third one without any component.
        world.Create();

        return world;
    }

    /// <summary>
    ///     Compares two worlds by their entity count and their component values.
    /// </summary>
    /// <param name="world">The expected world.</param>
    /// <param name="newWorld">The restored world.</param>
    private static void AssertWorld(World world, World newWorld)
    {
        That(newWorld.Size, Is.EqualTo(world.Size));

        // The order of archetypes and chunks may differ, hence both worlds are compared by their content.
        var expected = Collect(world);
        var actual = Collect(newWorld);

        That(actual.Structures, Is.EqualTo(expected.Structures));
        That(actual.Transforms, Is.EqualTo(expected.Transforms));
        That(actual.Names, Is.EqualTo(expected.Names));
    }

    /// <summary>
    ///     Collects the component structure and the component values of all entities of a world.
    /// </summary>
    /// <param name="world">The world.</param>
    /// <returns>The collected content.</returns>
    private static WorldContent Collect(World world)
    {
        var entities = new Entity[world.Size];
        world.GetEntities(new QueryDescription(), entities.AsSpan());

        var content = new WorldContent();
        foreach (var entity in entities)
        {
            var hasTransform = entity.Has<Transform>();
            var hasMetaData = entity.Has<MetaData>();

            content.Structures.Add($"{hasTransform}|{hasMetaData}");

            if (hasTransform)
            {
                var transform = entity.Get<Transform>();
                content.Transforms.Add((transform.X, transform.Y));
            }

            if (hasMetaData)
            {
                content.Names.Add(entity.Get<MetaData>().Name);
            }
        }

        content.Structures.Sort();
        content.Transforms.Sort();
        content.Names.Sort();
        return content;
    }

    /// <summary>
    ///     Rewrites the persisted component ids to simulate a process that registered the component types in another order.
    /// </summary>
    /// <param name="json">The persisted world.</param>
    /// <param name="map">Maps the persisted component ids and the position of a component to the simulated id.</param>
    /// <returns>The modified world.</returns>
    private static string RemapComponentIds(string json, Func<int[], int, int> map)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var archetypes = root["archetypes"]!.AsArray();

        // Collect the persisted component ids and simulate the ids of the writing process.
        var ids = new List<int>();
        foreach (var node in archetypes)
        {
            var components = node!["types"]!["components"]!.AsArray();
            foreach (var component in components)
            {
                var id = component!["id"]!.GetValue<int>();
                if (!ids.Contains(id))
                {
                    ids.Add(id);
                }
            }
        }

        // Ids are mapped by the position of the component they belong to.
        var idArray = ids.ToArray();
        var mapping = new Dictionary<int, int>();
        for (var index = 0; index < idArray.Length; index++)
        {
            mapping[idArray[index]] = map(idArray, index);
        }

        foreach (var node in archetypes)
        {
            var archetype = node!.AsObject();
            var components = archetype["types"]!["components"]!.AsArray();

            // The lookup array maps a component id to its array index, so it has to be rewritten along with the ids.
            var lookup = new int[mapping.Values.Max() + 1];
            Array.Fill(lookup, -1);

            for (var index = 0; index < components.Count; index++)
            {
                var component = components[index]!.AsObject();
                var id = mapping[component["id"]!.GetValue<int>()];
                component["id"] = id;
                lookup[id] = index;
            }

            // The lookup is persisted as an array object, hence its length has to be adjusted as well.
            var lookupNode = archetype["lookup"]!.AsObject();
            lookupNode["length"] = lookup.Length;
            lookupNode["items"] = new JsonArray(lookup.Select(value => (JsonNode)value).ToArray());
        }

        return root.ToJsonString();
    }
}

/// <summary>
///     The <see cref="WorldContent"/> class holds the component structure and the component values of a world.
/// </summary>
public sealed class WorldContent
{
    /// <summary>
    ///     The component structure of every entity.
    /// </summary>
    public List<string> Structures { get; } = new();

    /// <summary>
    ///     The <see cref="Transform"/> values of every entity that has one.
    /// </summary>
    public List<(float X, float Y)> Transforms { get; } = new();

    /// <summary>
    ///     The <see cref="MetaData"/> names of every entity that has one.
    /// </summary>
    public List<string> Names { get; } = new();
}
