using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace KK2Blender.Format
{
    /// <summary>
    /// Writes plain values as JSON: null, bool, string, numbers, IDictionary (string keys) and
    /// IEnumerable. Kept small and Unity-free so it builds for net35 and for the tests.
    /// Non-finite floats are written as null, since JSON has no NaN.
    /// </summary>
    public static class JsonWriter
    {
        public static string Write(object value, bool pretty = true)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, pretty, 0);
            if (pretty)
            {
                sb.Append('\n');
            }
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object value, bool pretty, int depth)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }
            if (value is string)
            {
                WriteString(sb, (string)value);
                return;
            }
            if (value is bool)
            {
                sb.Append((bool)value ? "true" : "false");
                return;
            }
            if (value is float)
            {
                WriteNumber(sb, (float)value);
                return;
            }
            if (value is double)
            {
                WriteNumber(sb, (double)value);
                return;
            }
            if (value is int || value is long || value is short || value is byte || value is uint || value is ulong || value is sbyte || value is ushort)
            {
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }
            if (value is Enum)
            {
                WriteString(sb, value.ToString());
                return;
            }
            var dict = value as IDictionary;
            if (dict != null)
            {
                WriteObject(sb, dict, pretty, depth);
                return;
            }
            var list = value as IEnumerable;
            if (list != null)
            {
                WriteArray(sb, list, pretty, depth);
                return;
            }
            throw new ArgumentException("JsonWriter can't write a " + value.GetType().FullName);
        }

        private static void WriteNumber(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d))
            {
                sb.Append("null");
                return;
            }
            sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void WriteNumber(StringBuilder sb, float f)
        {
            if (float.IsNaN(f) || float.IsInfinity(f))
            {
                sb.Append("null");
                return;
            }
            sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void Indent(StringBuilder sb, bool pretty, int depth)
        {
            if (!pretty)
            {
                return;
            }
            sb.Append('\n');
            sb.Append(' ', depth * 2);
        }

        private static bool IsFlat(IEnumerable list)
        {
            foreach (var item in list)
            {
                if (item is IDictionary || (item is IEnumerable && !(item is string)))
                {
                    return false;
                }
            }
            return true;
        }

        private static void WriteObject(StringBuilder sb, IDictionary dict, bool pretty, int depth)
        {
            sb.Append('{');
            bool first = true;
            foreach (DictionaryEntry e in dict)
            {
                if (!first)
                {
                    sb.Append(',');
                }
                first = false;
                Indent(sb, pretty, depth + 1);
                WriteString(sb, Convert.ToString(e.Key, CultureInfo.InvariantCulture));
                sb.Append(pretty ? ": " : ":");
                WriteValue(sb, e.Value, pretty, depth + 1);
            }
            if (!first)
            {
                Indent(sb, pretty, depth);
            }
            sb.Append('}');
        }

        private static void WriteArray(StringBuilder sb, IEnumerable list, bool pretty, int depth)
        {
            // Short arrays of numbers (vectors, matrices) stay on one line.
            bool flat = IsFlat(list);
            sb.Append('[');
            bool first = true;
            foreach (var item in list)
            {
                if (!first)
                {
                    sb.Append(flat && pretty ? ", " : ",");
                }
                first = false;
                if (!flat)
                {
                    Indent(sb, pretty, depth + 1);
                }
                WriteValue(sb, item, pretty, depth + 1);
            }
            if (!first && !flat)
            {
                Indent(sb, pretty, depth);
            }
            sb.Append(']');
        }

        public static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }
    }

    /// <summary>An insertion-ordered string-keyed map, so JSON keys keep the order they were added in.</summary>
    public sealed class JsonObject : IDictionary
    {
        private readonly List<string> keys = new List<string>();
        private readonly Dictionary<string, object> values = new Dictionary<string, object>();

        public object this[object key]
        {
            get { return values[(string)key]; }
            set { Add(key, value); }
        }

        public JsonObject Set(string key, object value)
        {
            Add(key, value);
            return this;
        }

        public void Add(object key, object value)
        {
            var k = (string)key;
            if (!values.ContainsKey(k))
            {
                keys.Add(k);
            }
            values[k] = value;
        }

        public bool Contains(object key) { return values.ContainsKey((string)key); }
        public void Clear() { keys.Clear(); values.Clear(); }
        public IDictionaryEnumerator GetEnumerator() { return new Enumerator(this); }
        IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }
        public void Remove(object key)
        {
            if (values.Remove((string)key))
            {
                keys.Remove((string)key);
            }
        }
        public bool IsFixedSize { get { return false; } }
        public bool IsReadOnly { get { return false; } }
        public ICollection Keys { get { return keys; } }
        public ICollection Values
        {
            get
            {
                var list = new List<object>();
                foreach (var k in keys) list.Add(values[k]);
                return list;
            }
        }
        public void CopyTo(Array array, int index) { throw new NotSupportedException(); }
        public int Count { get { return keys.Count; } }
        public bool IsSynchronized { get { return false; } }
        public object SyncRoot { get { return this; } }

        private sealed class Enumerator : IDictionaryEnumerator
        {
            private readonly JsonObject o;
            private int i = -1;
            public Enumerator(JsonObject o) { this.o = o; }
            public bool MoveNext() { i++; return i < o.keys.Count; }
            public void Reset() { i = -1; }
            public object Current { get { return Entry; } }
            public DictionaryEntry Entry { get { return new DictionaryEntry(o.keys[i], o.values[o.keys[i]]); } }
            public object Key { get { return o.keys[i]; } }
            public object Value { get { return o.values[o.keys[i]]; } }
        }
    }
}
