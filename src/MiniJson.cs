using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace DellFans
{
    // Minimal JSON for the service, CLI and pipe: replaces JavaScriptSerializer (System.Web.Extensions, several MiB resident).
    // Writer: public instance properties then public fields in declaration order, invariant culture, null as null, NaN/Infinity as null.
    // Reader: strict RFC 8259 (no comments, no trailing commas, no trailing text), depth <= 16, size limit, duplicate keys rejected,
    // unknown keys ignored (and reported). Integers inside Int32 come back as int, other whole numbers as long, the rest as double.
    public static class MiniJson
    {
        public const int MaxDepth = 16;
        public const int DefaultLimit = 4 * 1024 * 1024;

        // ---- writer ----
        public static string Serialize(object value)
        { StringBuilder text = new StringBuilder(256); Write(text, value, 0); return text.ToString(); }

        static void Write(StringBuilder text, object value, int depth)
        {
            if (depth > MaxDepth) throw new InvalidDataException("JSON nesting is too deep (or cyclic).");
            if (value == null || value is DBNull) { text.Append("null"); return; }
            string s = value as string;
            if (s != null) { Quote(text, s); return; }
            if (value is bool) { text.Append((bool)value ? "true" : "false"); return; }
            if (value is char) { Quote(text, value.ToString()); return; }
            if (value is double) { double d = (double)value; text.Append(double.IsNaN(d) || double.IsInfinity(d) ? "null" : d.ToString("R", CultureInfo.InvariantCulture)); return; }
            if (value is float) { float f = (float)value; text.Append(float.IsNaN(f) || float.IsInfinity(f) ? "null" : f.ToString("R", CultureInfo.InvariantCulture)); return; }
            if (value is Enum) { text.Append(Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)); return; }
            if (value is DateTime) { Quote(text, ((DateTime)value).ToString("o", CultureInfo.InvariantCulture)); return; }
            if (value is Guid) { Quote(text, value.ToString()); return; }
            if (value is sbyte || value is byte || value is short || value is ushort || value is int || value is uint || value is long || value is ulong || value is decimal)
            { text.Append(((IFormattable)value).ToString(null, CultureInfo.InvariantCulture)); return; }
            IDictionary dictionary = value as IDictionary;
            if (dictionary != null)
            {
                text.Append('{'); bool first = true;
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (!(entry.Key is string)) throw new InvalidDataException("JSON object keys must be strings.");
                    if (!first) text.Append(','); first = false;
                    Quote(text, (string)entry.Key); text.Append(':'); Write(text, entry.Value, depth + 1);
                }
                text.Append('}'); return;
            }
            IEnumerable sequence = value as IEnumerable;
            if (sequence != null)
            {
                text.Append('['); bool first = true;
                foreach (object item in sequence) { if (!first) text.Append(','); first = false; Write(text, item, depth + 1); }
                text.Append(']'); return;
            }
            text.Append('{'); bool any = false;
            foreach (Member member in Members(value.GetType()))
            {
                if (!member.CanRead) continue;
                if (any) text.Append(','); any = true;
                Quote(text, member.Name); text.Append(':'); Write(text, member.Get(value), depth + 1);
            }
            text.Append('}');
        }

        static void Quote(StringBuilder text, string value)
        {
            text.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': text.Append("\\\""); break;
                    case '\\': text.Append("\\\\"); break;
                    case '\b': text.Append("\\b"); break;
                    case '\f': text.Append("\\f"); break;
                    case '\n': text.Append("\\n"); break;
                    case '\r': text.Append("\\r"); break;
                    case '\t': text.Append("\\t"); break;
                    default:
                        if (c < 0x20) text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); else text.Append(c);
                        break;
                }
            }
            text.Append('"');
        }

        // ---- members ----
        sealed class Member
        {
            public string Name;
            public Type Type;
            public PropertyInfo Property;
            public FieldInfo Field;
            public bool CanRead { get { return Property != null ? Property.CanRead : true; } }
            public bool CanWrite { get { return Property != null ? Property.CanWrite && Property.GetSetMethod() != null : !Field.IsInitOnly && !Field.IsLiteral; } }
            public object Get(object target) { return Property != null ? Property.GetValue(target, null) : Field.GetValue(target); }
            public void Set(object target, object value) { if (Property != null) Property.SetValue(target, value, null); else Field.SetValue(target, value); }
        }
        static readonly Dictionary<Type, Member[]> cache = new Dictionary<Type, Member[]>();
        static Member[] Members(Type type)
        {
            lock (cache)
            {
                Member[] found;
                if (cache.TryGetValue(type, out found)) return found;
                List<Member> list = new List<Member>();
                foreach (PropertyInfo p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    if (p.GetIndexParameters().Length == 0 && (p.CanRead || p.CanWrite)) list.Add(new Member { Name = p.Name, Type = p.PropertyType, Property = p });
                foreach (FieldInfo f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                    list.Add(new Member { Name = f.Name, Type = f.FieldType, Field = f });
                return cache[type] = list.ToArray();
            }
        }

        // ---- reader ----
        public static object Parse(string text) { return Parse(text, DefaultLimit); }
        public static object Parse(string text, int maximumChars)
        {
            if (text == null) throw new ArgumentNullException("text");
            if (text.Length > maximumChars) throw new InvalidDataException("JSON exceeds its size limit.");
            Reader reader = new Reader(text);
            reader.SkipSpace();
            object value = reader.Value(0);
            reader.SkipSpace();
            if (!reader.End) throw reader.Fail("Unexpected text after the JSON value");
            return value;
        }

        sealed class Reader
        {
            readonly string s; int i;
            public Reader(string text) { s = text; i = text.Length > 0 && text[0] == '﻿' ? 1 : 0; }
            public bool End { get { return i >= s.Length; } }
            public Exception Fail(string what) { return new InvalidDataException("Invalid JSON at " + i + ": " + what + "."); }
            public void SkipSpace() { while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++; }
            public object Value(int depth)
            {
                if (End) throw Fail("unexpected end");
                char c = s[i];
                if (c == '{') return Object(depth);
                if (c == '[') return Array(depth);
                if (c == '"') return String();
                if (c == '-' || (c >= '0' && c <= '9')) return Number();
                if (Literal("true")) return true;
                if (Literal("false")) return false;
                if (Literal("null")) return null;
                throw Fail("unexpected character '" + c + "'");
            }
            bool Literal(string word)
            {
                if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;
                i += word.Length; return true;
            }
            object Object(int depth)
            {
                if (depth >= MaxDepth) throw Fail("nesting deeper than " + MaxDepth);
                Dictionary<string, object> map = new Dictionary<string, object>(StringComparer.Ordinal);
                i++; SkipSpace();
                if (!End && s[i] == '}') { i++; return map; }
                while (true)
                {
                    SkipSpace();
                    if (End || s[i] != '"') throw Fail("object key expected");
                    string key = String();
                    if (map.ContainsKey(key)) throw Fail("duplicate key \"" + key + "\"");
                    SkipSpace();
                    if (End || s[i] != ':') throw Fail("':' expected"); i++;
                    SkipSpace(); map[key] = Value(depth + 1); SkipSpace();
                    if (End) throw Fail("unexpected end");
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return map; }
                    throw Fail("',' or '}' expected");
                }
            }
            object Array(int depth)
            {
                if (depth >= MaxDepth) throw Fail("nesting deeper than " + MaxDepth);
                List<object> list = new List<object>();
                i++; SkipSpace();
                if (!End && s[i] == ']') { i++; return list; }
                while (true)
                {
                    SkipSpace(); list.Add(Value(depth + 1)); SkipSpace();
                    if (End) throw Fail("unexpected end");
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return list; }
                    throw Fail("',' or ']' expected");
                }
            }
            string String()
            {
                StringBuilder text = new StringBuilder(); i++;
                while (true)
                {
                    if (End) throw Fail("unterminated string");
                    char c = s[i++];
                    if (c == '"') return text.ToString();
                    if (c < 0x20) throw Fail("control character in string");
                    if (c != '\\') { text.Append(c); continue; }
                    if (End) throw Fail("unterminated escape");
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': text.Append('"'); break;
                        case '\\': text.Append('\\'); break;
                        case '/': text.Append('/'); break;
                        case 'b': text.Append('\b'); break;
                        case 'f': text.Append('\f'); break;
                        case 'n': text.Append('\n'); break;
                        case 'r': text.Append('\r'); break;
                        case 't': text.Append('\t'); break;
                        case 'u':
                            int code;
                            if (i + 4 > s.Length || !int.TryParse(s.Substring(i, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code) || s[i] == '+' || s[i] == '-' || s[i] == ' ')
                                throw Fail("bad \\u escape");
                            text.Append((char)code); i += 4; break;
                        default: throw Fail("bad escape");
                    }
                }
            }
            object Number()
            {
                int start = i;
                if (s[i] == '-') i++;
                if (End) throw Fail("digit expected");
                if (s[i] == '0') i++;
                else if (s[i] >= '1' && s[i] <= '9') while (!End && s[i] >= '0' && s[i] <= '9') i++;
                else throw Fail("digit expected");
                bool whole = true;
                if (!End && s[i] == '.')
                {
                    whole = false; i++; int digits = 0;
                    while (!End && s[i] >= '0' && s[i] <= '9') { i++; digits++; }
                    if (digits == 0) throw Fail("digit expected after '.'");
                }
                if (!End && (s[i] == 'e' || s[i] == 'E'))
                {
                    whole = false; i++;
                    if (!End && (s[i] == '+' || s[i] == '-')) i++;
                    int digits = 0;
                    while (!End && s[i] >= '0' && s[i] <= '9') { i++; digits++; }
                    if (digits == 0) throw Fail("digit expected in exponent");
                }
                string token = s.Substring(start, i - start);
                if (whole)
                {
                    long l;
                    if (long.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out l)) return l >= int.MinValue && l <= int.MaxValue ? (object)(int)l : l;
                }
                double d = double.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture);
                if (double.IsInfinity(d)) throw Fail("number out of range");
                return d;
            }
        }

        // ---- binder ----
        public static T Deserialize<T>(string text) { return Deserialize<T>(text, DefaultLimit); }
        public static T Deserialize<T>(string text, int maximumChars)
        { List<string> ignored; return Deserialize<T>(text, maximumChars, out ignored); }
        // `unknownKeys` lists the keys (dotted from the root) that matched no public member; callers that accept commands may refuse them.
        public static T Deserialize<T>(string text, int maximumChars, out List<string> unknownKeys)
        {
            List<string> unknown = new List<string>();
            object value = Bind(Parse(text, maximumChars), typeof(T), "", unknown);
            unknownKeys = unknown;
            return (T)value;
        }

        static object Bind(object node, Type type, string path, List<string> unknown)
        {
            Type nullable = Nullable.GetUnderlyingType(type);
            if (node == null)
            {
                if (type.IsValueType && nullable == null) throw new InvalidDataException("JSON null where " + type.Name + " is required at " + Where(path) + ".");
                return null;
            }
            if (nullable != null) type = nullable;
            if (type == typeof(object)) return node;
            if (type == typeof(string)) { if (node is string) return node; throw Wrong(type, path); }
            if (type == typeof(bool)) { if (node is bool) return node; throw Wrong(type, path); }
            if (type == typeof(DateTime))
            {
                DateTime when;
                if (node is string && DateTime.TryParse((string)node, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out when)) return when;
                throw Wrong(type, path);
            }
            if (type.IsEnum)
            {
                if (node is string) { try { return Enum.Parse(type, (string)node, true); } catch (ArgumentException) { throw Wrong(type, path); } }
                if (node is int || node is long) return Enum.ToObject(type, node);
                throw Wrong(type, path);
            }
            if (type.IsPrimitive || type == typeof(decimal))
            {
                if (!(node is int || node is long || node is double)) throw Wrong(type, path);
                try
                {
                    if (node is double && type != typeof(double) && type != typeof(float) && type != typeof(decimal))
                    {
                        double d = (double)node;
                        if (d != Math.Floor(d)) throw Wrong(type, path);
                    }
                    return Convert.ChangeType(node, type, CultureInfo.InvariantCulture);
                }
                catch (OverflowException) { throw Wrong(type, path); }
                catch (InvalidCastException) { throw Wrong(type, path); }
            }
            IDictionary<string, object> map = node as IDictionary<string, object>;
            List<object> list = node as List<object>;
            if (type.IsArray)
            {
                if (list == null) throw Wrong(type, path);
                Type element = type.GetElementType(); Array array = Array.CreateInstance(element, list.Count);
                for (int n = 0; n < list.Count; n++) array.SetValue(Bind(list[n], element, path + "[" + n + "]", unknown), n);
                return array;
            }
            if (type.IsGenericType)
            {
                Type definition = type.GetGenericTypeDefinition(); Type[] arguments = type.GetGenericArguments();
                if (definition == typeof(Dictionary<,>) || definition == typeof(IDictionary<,>))
                {
                    if (map == null || arguments[0] != typeof(string)) throw Wrong(type, path);
                    IDictionary result = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(arguments));
                    foreach (KeyValuePair<string, object> pair in map) result[pair.Key] = Bind(pair.Value, arguments[1], Join(path, pair.Key), unknown);
                    return result;
                }
                if (definition == typeof(List<>) || definition == typeof(IList<>) || definition == typeof(IEnumerable<>) || definition == typeof(ICollection<>))
                {
                    if (list == null) throw Wrong(type, path);
                    IList result = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(arguments[0]));
                    for (int n = 0; n < list.Count; n++) result.Add(Bind(list[n], arguments[0], path + "[" + n + "]", unknown));
                    return result;
                }
            }
            if (map == null) throw Wrong(type, path);
            object target;
            try { target = Activator.CreateInstance(type, true); }
            catch (MissingMethodException) { throw Wrong(type, path); }
            Member[] members = Members(type);
            foreach (KeyValuePair<string, object> pair in map)
            {
                Member match = members.FirstOrDefault(m => string.Equals(m.Name, pair.Key, StringComparison.OrdinalIgnoreCase));
                if (match == null) { unknown.Add(Join(path, pair.Key)); continue; }
                if (match.CanWrite) match.Set(target, Bind(pair.Value, match.Type, Join(path, pair.Key), unknown));
            }
            return target;
        }
        static string Join(string path, string key) { return path.Length == 0 ? key : path + "." + key; }
        static string Where(string path) { return path.Length == 0 ? "the root" : path; }
        static Exception Wrong(Type type, string path) { return new InvalidDataException("JSON value at " + Where(path) + " is not a valid " + type.Name + "."); }
    }
}
