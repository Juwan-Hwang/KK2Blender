using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace KK2Blender.Format
{
    /// <summary>
    /// A small JSON reader for the exporter's own data files (the shader property tables). Objects
    /// become <see cref="Dictionary{TKey,TValue}"/> of string to object (insertion order is not kept),
    /// arrays <see cref="List{T}"/> of object, numbers double, and true/false/null bool and null.
    /// </summary>
    public static class JsonReader
    {
        public static object Parse(string text)
        {
            int at = 0;
            var value = Value(text, ref at);
            Skip(text, ref at);
            if (at != text.Length)
            {
                throw new FormatException("Unexpected text after the JSON value at " + at);
            }
            return value;
        }

        private static void Skip(string s, ref int at)
        {
            while (at < s.Length && (s[at] == ' ' || s[at] == '\t' || s[at] == '\n' || s[at] == '\r' || s[at] == '\uFEFF'))
            {
                at++;
            }
        }

        private static object Value(string s, ref int at)
        {
            Skip(s, ref at);
            if (at >= s.Length)
            {
                throw new FormatException("Unexpected end of JSON");
            }
            char c = s[at];
            if (c == '{')
            {
                at++;
                var o = new Dictionary<string, object>(StringComparer.Ordinal);
                Skip(s, ref at);
                if (at < s.Length && s[at] == '}')
                {
                    at++;
                    return o;
                }
                while (true)
                {
                    Skip(s, ref at);
                    string key = String(s, ref at);
                    Skip(s, ref at);
                    Expect(s, ref at, ':');
                    o[key] = Value(s, ref at);
                    Skip(s, ref at);
                    if (at < s.Length && s[at] == ',')
                    {
                        at++;
                        continue;
                    }
                    Expect(s, ref at, '}');
                    return o;
                }
            }
            if (c == '[')
            {
                at++;
                var list = new List<object>();
                Skip(s, ref at);
                if (at < s.Length && s[at] == ']')
                {
                    at++;
                    return list;
                }
                while (true)
                {
                    list.Add(Value(s, ref at));
                    Skip(s, ref at);
                    if (at < s.Length && s[at] == ',')
                    {
                        at++;
                        continue;
                    }
                    Expect(s, ref at, ']');
                    return list;
                }
            }
            if (c == '"')
            {
                return String(s, ref at);
            }
            if (Word(s, ref at, "true"))
            {
                return true;
            }
            if (Word(s, ref at, "false"))
            {
                return false;
            }
            if (Word(s, ref at, "null"))
            {
                return null;
            }
            int start = at;
            while (at < s.Length && "+-0123456789.eE".IndexOf(s[at]) >= 0)
            {
                at++;
            }
            double d;
            if (at == start || !double.TryParse(s.Substring(start, at - start), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
            {
                throw new FormatException("Bad JSON value at " + start);
            }
            return d;
        }

        private static bool Word(string s, ref int at, string word)
        {
            if (string.CompareOrdinal(s, at, word, 0, word.Length) == 0)
            {
                at += word.Length;
                return true;
            }
            return false;
        }

        private static void Expect(string s, ref int at, char c)
        {
            if (at >= s.Length || s[at] != c)
            {
                throw new FormatException("Expected '" + c + "' at " + at);
            }
            at++;
        }

        private static string String(string s, ref int at)
        {
            Expect(s, ref at, '"');
            var sb = new StringBuilder();
            while (at < s.Length)
            {
                char c = s[at++];
                if (c == '"')
                {
                    return sb.ToString();
                }
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }
                if (at >= s.Length)
                {
                    break;
                }
                char e = s[at++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (at + 4 > s.Length)
                        {
                            throw new FormatException("Bad \\u escape at " + at);
                        }
                        sb.Append((char)int.Parse(s.Substring(at, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        at += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }
            throw new FormatException("Unterminated JSON string");
        }
    }
}
