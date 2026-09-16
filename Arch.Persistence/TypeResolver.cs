using System.Reflection;

namespace Arch.Persistence;

/// <summary>
///     The <see cref="TypeResolver"/> class resolves <see cref="Type"/>s from their persisted names.
/// </summary>
/// <remarks>
///     Component ids are assigned in the order in which component types are first registered during runtime.
///     They therefore differ between processes, which is why persisted data has to be resolved by its type name
///     instead of its id. The stored assembly qualified name may however point to an assembly that was renamed,
///     rebuilt with a different version or moved, so a fallback by full name is required.
/// </remarks>
internal static class TypeResolver
{
    /// <summary>
    ///     Resolves a <see cref="Type"/> by its assembly qualified name.
    /// </summary>
    /// <param name="typeName">Its name, e.g. a <see cref="Type.AssemblyQualifiedName"/>.</param>
    /// <returns>The resolved <see cref="Type"/>, or null if it could not be resolved.</returns>
    internal static Type? Resolve(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return null;
        }

        // Fast path, the type is found within its own assembly.
        var type = Type.GetType(typeName, false);
        if (type is not null)
        {
            return type;
        }

        // The assembly may have been renamed or its version changed, search the loaded assemblies by full name.
        var fullName = GetFullName(typeName);
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            type = assembly.GetType(fullName, false);
            if (type is not null)
            {
                return type;
            }
        }

        return null;
    }

    /// <summary>
    ///     Strips the assembly part of an assembly qualified name, ignoring assembly names of generic arguments.
    /// </summary>
    /// <param name="typeName">The assembly qualified name.</param>
    /// <returns>The full name of the type.</returns>
    private static string GetFullName(string typeName)
    {
        var depth = 0;
        for (var index = 0; index < typeName.Length; index++)
        {
            var character = typeName[index];
            switch (character)
            {
                case '<' or '[':
                    depth++;
                    break;
                case '>' or ']':
                    depth--;
                    break;
                case ',' when depth is 0:
                    return typeName.Substring(0, index).Trim();
            }
        }

        return typeName.Trim();
    }
}
