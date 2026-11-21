using System.Text;

namespace Switchboard.Core.Ddc;

/// <summary>One VCP code from a capabilities string, with its value list if it has one.</summary>
public sealed record VcpCapability(byte Code, IReadOnlyList<uint>? Values);

/// <summary>
/// Parses MCCS capabilities strings such as
/// <c>(prot(monitor)type(LCD)model(X)cmds(01 02 03)vcp(02 10 12 60(0F 11 12) D6(01 04))mccs_ver(2.2))</c>.
/// Monitors are sloppy about spacing and nesting, so the parser is forgiving.
/// </summary>
public static class CapabilitiesParser
{
    /// <summary>Contents of the first <c>name(...)</c> group, or null.</summary>
    public static string? GetGroup(string? caps, string name)
    {
        if (string.IsNullOrEmpty(caps)) return null;
        int from = 0;
        while (true)
        {
            int at = caps.IndexOf(name + "(", from, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return null;
            if (at > 0 && (char.IsLetterOrDigit(caps[at - 1]) || caps[at - 1] == '_'))
            {
                from = at + 1; // part of a longer name, e.g. "vcpname(" when looking for "name("
                continue;
            }
            int start = at + name.Length + 1;
            int depth = 1;
            for (int i = start; i < caps.Length; i++)
            {
                if (caps[i] == '(') depth++;
                else if (caps[i] == ')' && --depth == 0) return caps.Substring(start, i - start);
            }
            return caps.Substring(start); // unbalanced; take the rest
        }
    }

    /// <summary>Codes and value lists from the inside of a vcp(...) group.</summary>
    public static IReadOnlyList<VcpCapability> ParseVcp(string? vcpGroup)
    {
        var result = new List<VcpCapability>();
        if (vcpGroup is null) return result;
        int i = 0, n = vcpGroup.Length;
        while (i < n)
        {
            if (!IsHex(vcpGroup[i])) { i++; continue; }
            int start = i;
            while (i < n && i - start < 2 && IsHex(vcpGroup[i])) i++;
            byte code = Convert.ToByte(vcpGroup.Substring(start, i - start), 16);
            List<uint>? values = null;
            int j = i;
            while (j < n && vcpGroup[j] == ' ') j++;
            if (j < n && vcpGroup[j] == '(')
            {
                int depth = 1, valuesStart = j + 1, k = valuesStart;
                while (k < n && depth > 0)
                {
                    if (vcpGroup[k] == '(') depth++;
                    else if (vcpGroup[k] == ')') depth--;
                    k++;
                }
                int valuesEnd = depth == 0 ? k - 1 : k;
                values = ParseHexList(vcpGroup.Substring(valuesStart, valuesEnd - valuesStart));
                i = k;
            }
            result.Add(new VcpCapability(code, values));
        }
        return result;
    }

    /// <summary>The input values listed for VCP 0x60, or an empty list if the string doesn't list any.</summary>
    public static IReadOnlyList<uint> GetInputSources(string? caps)
    {
        foreach (var code in ParseVcp(GetGroup(caps, "vcp")))
        {
            if (code.Code == VcpCodes.InputSource && code.Values is not null)
                return code.Values.Distinct().ToList();
        }
        return [];
    }

    /// <summary>The VCP codes a capabilities string lists (brightness 10, volume 62...). Empty if it lists none.</summary>
    public static IReadOnlySet<byte> GetSupportedCodes(string? caps) =>
        ParseVcp(GetGroup(caps, "vcp")).Select(c => c.Code).ToHashSet();

    /// <summary>The model(...) name, if any.</summary>
    public static string? GetModel(string? caps)
    {
        string? model = GetGroup(caps, "model")?.Trim();
        return string.IsNullOrEmpty(model) ? null : model;
    }

    // Anything that isn't a hex digit separates values; run-together digits are split into pairs.
    private static List<uint> ParseHexList(string text)
    {
        var values = new List<uint>();
        var token = new StringBuilder();
        for (int i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && IsHex(text[i])) { token.Append(text[i]); continue; }
            if (token.Length == 0) continue;
            string t = token.ToString();
            token.Clear();
            if (t.Length <= 2) { values.Add(Convert.ToUInt32(t, 16)); continue; }
            for (int p = 0; p + 1 < t.Length; p += 2) values.Add(Convert.ToUInt32(t.Substring(p, 2), 16));
        }
        return values;
    }

    private static bool IsHex(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');
}
