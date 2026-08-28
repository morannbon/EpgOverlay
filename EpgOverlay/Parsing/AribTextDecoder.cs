using System.Text;
namespace EpgOverlay.Parsing;

internal sealed class AribTextDecoder
{
    public string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) return string.Empty;
        return Clean(DecodeFallback(bytes));
    }

    private static string DecodeFallback(ReadOnlySpan<byte> bytes)
    {
        var builder = new StringBuilder(bytes.Length);
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            if (b is >= 0x20 and <= 0x7e)
            {
                builder.Append((char)b);
                continue;
            }

            if (b == 0x0a || b == 0x0d)
            {
                builder.Append(' ');
                continue;
            }

            if (b >= 0xa1)
            {
                builder.Append('□');
            }
        }

        return builder.ToString();
    }

    private static string Clean(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var builder = new StringBuilder(text.Length);
        var lastSpace = false;
        foreach (var c in text)
        {
            if (char.IsControl(c)) continue;
            if (char.IsWhiteSpace(c))
            {
                if (!lastSpace) builder.Append(' ');
                lastSpace = true;
                continue;
            }
            builder.Append(c);
            lastSpace = false;
        }
        return builder.ToString().Trim();
    }
}
