using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace EndpointToolbelt.Engine;

/// <summary>One JSON value of the request file, kept as written so each field can be judged on its own.</summary>
public sealed class JsonNode
{
    public enum NodeKind
    {
        Map,
        Array,
        Text,
        Number,
        Boolean,
        Null,
    }

    private static readonly char[] NotWhole = { '.', 'e', 'E' };

    private readonly List<KeyValuePair<string, JsonNode>> _members = new();

    public JsonNode(NodeKind kind, string text = "")
    {
        Kind = kind;
        Text = text ?? "";
    }

    public NodeKind Kind { get; }

    /// <summary>The text of a string, number ("12", "2.5") or boolean ("true").</summary>
    public string Text { get; }

    internal void Add(string name, JsonNode value) => _members.Add(new KeyValuePair<string, JsonNode>(name, value));

    /// <summary>A member of an object (name matched ignoring case, as PowerShell does), or null.</summary>
    public JsonNode Get(string name)
    {
        if (Kind != NodeKind.Map) { return null; }
        foreach (var pair in _members)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) { return pair.Value; }
        }
        return null;
    }

    /// <summary>The elements of an array, in order; none for any other kind.</summary>
    public IEnumerable<JsonNode> Items => Kind == NodeKind.Array ? _members.Select(m => m.Value) : Enumerable.Empty<JsonNode>();

    /// <summary>
    /// The value as text, the way PowerShell's [string] cast shows what ConvertFrom-Json returned:
    /// a boolean as "True" or "False", a whole number in digits, any other number as .NET writes
    /// a double, null and objects as an empty string.
    /// </summary>
    public string AsText()
    {
        switch (Kind)
        {
            case NodeKind.Text:
                return Text;
            case NodeKind.Boolean:
                return Text == "true" ? "True" : "False";
            case NodeKind.Number:
                if (long.TryParse(Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole)) { return whole.ToString(CultureInfo.InvariantCulture); }
                if (double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) { return number.ToString(CultureInfo.InvariantCulture); }
                return Text;
            default:
                return "";
        }
    }

    /// <summary>
    /// A whole number written as one in JSON (no fraction or exponent), as PowerShell's
    /// ConvertFrom-Json returns an Int32 or Int64 for it; null for anything else.
    /// </summary>
    public long? AsWholeNumber()
    {
        if (Kind != NodeKind.Number || Text.IndexOfAny(NotWhole) >= 0) { return null; }
        return long.TryParse(Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary>Parses JSON text; throws when it is not JSON.</summary>
    public static JsonNode Parse(string text)
    {
        var bytes = new UTF8Encoding(false).GetBytes(text);
        using var reader = JsonReaderWriterFactory.CreateJsonReader(bytes, XmlDictionaryReaderQuotas.Max);
        return FromElement(XElement.Load(reader));
    }

    private static JsonNode FromElement(XElement element)
    {
        var type = (string)element.Attribute("type") ?? "string";
        switch (type)
        {
            case "object":
            {
                var node = new JsonNode(NodeKind.Map);
                foreach (var child in element.Elements())
                {
                    // A name that is not a valid XML name is kept in an "item" attribute.
                    node.Add((string)child.Attribute("item") ?? child.Name.LocalName, FromElement(child));
                }
                return node;
            }
            case "array":
            {
                var node = new JsonNode(NodeKind.Array);
                foreach (var child in element.Elements()) { node.Add("", FromElement(child)); }
                return node;
            }
            case "number":
                return new JsonNode(NodeKind.Number, element.Value.Trim());
            case "boolean":
                return new JsonNode(NodeKind.Boolean, element.Value.Trim());
            case "null":
                return new JsonNode(NodeKind.Null);
            default:
                return new JsonNode(NodeKind.Text, element.Value);
        }
    }
}

/// <summary>The request file the service wrote in its SYSTEM-only working folder.</summary>
public static class RequestFile
{
    /// <summary>The request as JSON, or null when the file is missing or not JSON.</summary>
    public static JsonNode Read(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// A whole number from the request, or <paramref name="fallback"/> when it is missing, not a
    /// number or too large for an Int32.
    /// </summary>
    public static int ToInt(JsonNode value, int fallback)
    {
        if (value == null || value.Kind == JsonNode.NodeKind.Null) { return fallback; }
        return int.TryParse(value.AsText(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) ? number : fallback;
    }

    internal static bool Has(JsonNode request, string name) => request?.Get(name) != null;

    internal static string Text(JsonNode request, string name) => request?.Get(name)?.AsText() ?? "";
}
