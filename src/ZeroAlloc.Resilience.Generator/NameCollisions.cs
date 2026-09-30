using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace ZeroAlloc.Resilience.Generator;

/// <summary>
/// The generated policies class, proxy and <c>Add...Resilience</c> methods are named after the
/// interface alone and emitted at the top of its namespace, so two interfaces of one name nested
/// in different types would declare them twice (#209). Such interfaces get names qualified with
/// their containing types, as in <c>First_FooResiliencePolicies</c>. Every other interface keeps
/// its names, so code that compiles today is unchanged.
/// </summary>
internal static class NameCollisions
{
    /// <summary>
    /// The containing types joined with underscores, each followed by one, as in
    /// <c>Outer_Inner_</c>; empty for an interface at the top of its namespace.
    /// </summary>
    public static string QualifyingPrefix(INamedTypeSymbol iface)
    {
        var sb = new StringBuilder();
        for (var type = iface.ContainingType; type is not null; type = type.ContainingType)
            sb.Insert(0, type.Name + "_");
        return sb.ToString();
    }

    /// <summary>
    /// The names <paramref name="model"/> declares, or null when it generates no code: an
    /// interface with an error, such as ZR0007, takes no name.
    /// </summary>
    public static NameKey? KeyOf(ResilienceModel model)
    {
        foreach (var diagnostic in model.Diagnostics)
        {
            if (diagnostic.Severity == DiagnosticSeverity.Error) return null;
        }
        return new NameKey(model.HintName, model.Namespace, model.RegistrationName, model.ProxyClassName, model.QualifyingPrefix);
    }

    /// <summary>
    /// The hint names of the interfaces to qualify. An interface is qualified when another in its
    /// namespace would declare the same policies class, which also names its DI methods, or the
    /// same proxy. A qualified name can equal another interface's own name, as
    /// <c>First_Foo</c> for <c>First.IFoo</c> and <c>X.IFirst_Foo</c>, so this repeats until
    /// nothing more changes. An interface at the top of its namespace has nothing to qualify
    /// with and keeps its names.
    /// </summary>
    public static QualifiedHintNames Find(ImmutableArray<NameKey> keys)
    {
        var qualified = new HashSet<string>(StringComparer.Ordinal);
        while (QualifyCollisions(keys, qualified, static (k, q) => RegistrationName(k, q))
             | QualifyCollisions(keys, qualified, static (k, q) => ProxyClassName(k, q)))
        {
        }

        return new QualifiedHintNames(qualified.OrderBy(static h => h, StringComparer.Ordinal).ToImmutableArray());
    }

    // One pass over one kind of name: every interface that shares its current name with another
    // in its namespace, and has containing types, is added to qualified. True when any was.
    private static bool QualifyCollisions(
        ImmutableArray<NameKey> keys, HashSet<string> qualified, Func<NameKey, HashSet<string>, string> nameOf)
    {
        var byName = new Dictionary<(string? Namespace, string Name), List<NameKey>>();
        foreach (var key in keys)
        {
            var name = (key.Namespace, nameOf(key, qualified));
            if (!byName.TryGetValue(name, out var group))
                byName.Add(name, group = new List<NameKey>());
            group.Add(key);
        }

        var changed = false;
        foreach (var group in byName.Values)
        {
            if (group.Count < 2) continue;
            for (var i = 0; i < group.Count; i++)
            {
                if (group[i].Prefix.Length > 0 && qualified.Add(group[i].HintName)) changed = true;
            }
        }
        return changed;
    }

    /// <summary><paramref name="model"/> with its three names qualified.</summary>
    public static ResilienceModel Qualify(ResilienceModel model) => model with
    {
        PoliciesClassName = model.QualifyingPrefix + model.PoliciesClassName,
        ProxyClassName = model.QualifyingPrefix + model.ProxyClassName,
        RegistrationName = model.QualifyingPrefix + model.RegistrationName,
    };

    private static string RegistrationName(NameKey key, HashSet<string> qualified) =>
        qualified.Contains(key.HintName) ? key.Prefix + key.RegistrationName : key.RegistrationName;

    private static string ProxyClassName(NameKey key, HashSet<string> qualified) =>
        qualified.Contains(key.HintName) ? key.Prefix + key.ProxyClassName : key.ProxyClassName;
}

/// <summary>The names one interface's generated code declares in its namespace.</summary>
internal sealed record NameKey(string HintName, string? Namespace, string RegistrationName, string ProxyClassName, string Prefix);

/// <summary>Sorted hint names, compared by value so the step that makes them can stay cached.</summary>
internal sealed class QualifiedHintNames : IEquatable<QualifiedHintNames>
{
    private readonly ImmutableArray<string> _hintNames;

    public QualifiedHintNames(ImmutableArray<string> hintNames) => _hintNames = hintNames;

    public bool Contains(string hintName) => _hintNames.Contains(hintName, StringComparer.Ordinal);

    public bool Equals(QualifiedHintNames? other) =>
        other is not null && _hintNames.SequenceEqual(other._hintNames, StringComparer.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as QualifiedHintNames);

    public override int GetHashCode()
    {
        var hash = 17;
        foreach (var hintName in _hintNames)
            hash = unchecked((hash * 31) + StringComparer.Ordinal.GetHashCode(hintName));
        return hash;
    }
}
