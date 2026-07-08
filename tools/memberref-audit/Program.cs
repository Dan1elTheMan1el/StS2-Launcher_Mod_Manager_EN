// MemberRef audit: enumerate every MemberRef in <consumer.dll> that resolves into
// assembly "sts2", then verify a matching MethodDef/FieldDef exists in <target sts2.dll>.
// Usage: audit <consumer.dll> <target-sts2.dll> [scopeAssemblyName=sts2]
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: audit <consumer.dll> <target-sts2.dll> [scopeName]");
    return 2;
}
string scopeName = args.Length > 2 ? args[2] : "sts2";

using var consumerPe = new PEReader(File.OpenRead(args[0]));
var c = consumerPe.GetMetadataReader();
using var targetPe = new PEReader(File.OpenRead(args[1]));
var t = targetPe.GetMetadataReader();

var provider = new SigStringProvider();

// ---- index target: full type name -> members --------------------------------
var targetTypes = new Dictionary<string, (HashSet<string> methods, HashSet<string> fields)>();
foreach (var tdHandle in t.TypeDefinitions)
{
    var td = t.GetTypeDefinition(tdHandle);
    string full = Sig.TypeDefFullName(t, td);
    if (!targetTypes.TryGetValue(full, out var bucket))
    {
        bucket = (new HashSet<string>(), new HashSet<string>());
        targetTypes[full] = bucket;
    }
    foreach (var mh in td.GetMethods())
    {
        var md = t.GetMethodDefinition(mh);
        var sig = md.DecodeSignature(provider, null);
        bucket.methods.Add(t.GetString(md.Name) + "|" + Sig.Key(sig));
    }
    foreach (var fh in td.GetFields())
    {
        var fd = t.GetFieldDefinition(fh);
        bucket.fields.Add(t.GetString(fd.Name) + "|" + Sig.Strip(fd.DecodeSignature(provider, null)));
    }
}

// ---- walk consumer MemberRefs ------------------------------------------------
int total = 0, missing = 0;
foreach (var mrHandle in c.MemberReferences)
{
    var mr = c.GetMemberReference(mrHandle);
    string? typeName = ParentTypeFullName(c, mr.Parent, provider, out string? scope);
    if (typeName == null || scope != scopeName) continue;
    total++;

    string memberName = c.GetString(mr.Name);
    bool found;
    string detail;
    if (mr.GetKind() == MemberReferenceKind.Method)
    {
        var sig = mr.DecodeMethodSignature(provider, null);
        detail = Render(memberName, sig);
        found = targetTypes.TryGetValue(typeName, out var bucket)
            && bucket.methods.Contains(memberName + "|" + Sig.Key(sig));
    }
    else
    {
        string fieldType = Sig.Strip(mr.DecodeFieldSignature(provider, null));
        detail = fieldType + " " + memberName;
        found = targetTypes.TryGetValue(typeName, out var bucket)
            && bucket.fields.Contains(memberName + "|" + fieldType);
    }

    if (!found)
    {
        missing++;
        Console.WriteLine($"MISSING  {typeName} :: {detail}");
    }
}
Console.WriteLine($"--- audited {total} {scopeName}-scoped MemberRefs, {missing} missing ---");
return missing == 0 ? 0 : 1;

// ---- helpers -------------------------------------------------------------------
static string Render(string name, MethodSignature<string> s) =>
    $"{Sig.Strip(s.ReturnType)} {name}{(s.GenericParameterCount > 0 ? $"`{s.GenericParameterCount}" : "")}({string.Join(", ", s.ParameterTypes.Select(Sig.Strip))})";

static string? ParentTypeFullName(MetadataReader r, EntityHandle parent, SigStringProvider provider, out string? scope)
{
    scope = null;
    switch (parent.Kind)
    {
        case HandleKind.TypeReference:
            return Sig.TypeRefFullName(r, (TypeReferenceHandle)parent, out scope);
        case HandleKind.TypeSpecification:
        {
            var ts = r.GetTypeSpecification((TypeSpecificationHandle)parent);
            string decoded = ts.DecodeSignature(provider, null); // e.g. "Ns.Type`1@sts2<Boolean>"
            int at = decoded.IndexOf('@');
            if (at < 0) return null;
            string full = decoded[..at];
            int end = at + 1;
            while (end < decoded.Length && decoded[end] != '<' && decoded[end] != ','
                   && decoded[end] != '>' && decoded[end] != '[' && decoded[end] != '&')
                end++;
            scope = decoded[(at + 1)..end];
            return full;
        }
        default:
            return null; // MethodDef / ModuleRef parents: not cross-assembly, skip
    }
}

static class Sig
{
    public static string Key(MethodSignature<string> s) =>
        $"g{s.GenericParameterCount}({string.Join(",", s.ParameterTypes.Select(Strip))}):{Strip(s.ReturnType)}";

    // remove "@scope" suffixes for stable cross-side compare
    public static string Strip(string s)
    {
        int at;
        while ((at = s.IndexOf('@')) >= 0)
        {
            int end = at + 1;
            while (end < s.Length && s[end] != '<' && s[end] != ',' && s[end] != '>'
                   && s[end] != '[' && s[end] != '&' && s[end] != '*' && s[end] != '+' && s[end] != ')')
                end++;
            s = s[..at] + s[end..];
        }
        return s;
    }

    public static string TypeDefFullName(MetadataReader r, TypeDefinition td)
    {
        string name = r.GetString(td.Name);
        var declaring = td.GetDeclaringType();
        if (!declaring.IsNil)
            return TypeDefFullName(r, r.GetTypeDefinition(declaring)) + "+" + name;
        string ns = r.GetString(td.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    public static string TypeRefFullName(MetadataReader r, TypeReferenceHandle h, out string? scope)
    {
        var tr = r.GetTypeReference(h);
        string name = r.GetString(tr.Name);
        switch (tr.ResolutionScope.Kind)
        {
            case HandleKind.AssemblyReference:
                scope = r.GetString(r.GetAssemblyReference((AssemblyReferenceHandle)tr.ResolutionScope).Name);
                string ns = r.GetString(tr.Namespace);
                return ns.Length == 0 ? name : ns + "." + name;
            case HandleKind.TypeReference: // nested type
                string outer = TypeRefFullName(r, (TypeReferenceHandle)tr.ResolutionScope, out scope);
                return outer + "+" + name;
            default:
                scope = null;
                return name;
        }
    }
}

// Renders types as strings. TypeRefs carry an "@scope" suffix so TypeSpec parents can
// recover their resolution scope; Sig.Strip removes those for comparisons.
class SigStringProvider : ISignatureTypeProvider<string, object?>
{
    public string GetPrimitiveType(PrimitiveTypeCode code) => code.ToString();
    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        => Sig.TypeDefFullName(reader, reader.GetTypeDefinition(handle));
    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        string full = Sig.TypeRefFullName(reader, handle, out string? scope);
        return scope is { Length: > 0 } ? full + "@" + scope : full;
    }
    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', shape.Rank - 1) + "]";
    public string GetByReferenceType(string elementType) => elementType + "&";
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetGenericInstantiation(string genericType, System.Collections.Immutable.ImmutableArray<string> typeArguments)
        => genericType + "<" + string.Join(",", typeArguments) + ">";
    public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
    public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired)
        => (isRequired ? "modreq(" : "modopt(") + modifier + ")" + unmodifiedType;
    public string GetPinnedType(string elementType) => elementType;
    public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
    public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
}
