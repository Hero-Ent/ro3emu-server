using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Ro3Android.Updater
{
    /// <summary>
    /// A small JSON reader and writer: objects become Dictionary&lt;string, object&gt;, arrays List&lt;object&gt;, numbers
    /// long (or double), plus string, bool and null. The app cannot use System.Text.Json (the game's copy is stripped) and
    /// a reflection serializer would need IL2CPP to keep every type.
    /// </summary>
    public static class MiniJson
    {
        public static object Parse(byte[] utf8) => Parse(Encoding.UTF8.GetString(utf8));

        public static object Parse(string text)
        {
            var reader = new Reader(text);
            reader.SkipSpace();
            object value = reader.Value();
            reader.SkipSpace();
            if (reader.Pos != text.Length) throw reader.Error("trailing data");
            return value;
        }

        public static Dictionary<string, object> Obj(object value, string name)
        {
            var obj = value as Dictionary<string, object> ?? throw new InvalidDataException("JSON: expected an object");
            return obj.TryGetValue(name, out var v) ? v as Dictionary<string, object> : null;
        }

        public static List<object> Arr(Dictionary<string, object> obj, string name) =>
            obj.TryGetValue(name, out var v) && v is List<object> list ? list : throw new InvalidDataException($"JSON: '{name}' is not an array");

        public static string Str(Dictionary<string, object> obj, string name) =>
            obj != null && obj.TryGetValue(name, out var v) && v is string s ? s : "";

        public static long Long(Dictionary<string, object> obj, string name) =>
            obj != null && obj.TryGetValue(name, out var v) ? v is long l ? l : v is double d ? (long)d : 0 : 0;

        public static bool Bool(Dictionary<string, object> obj, string name) =>
            obj != null && obj.TryGetValue(name, out var v) && v is bool b && b;

        sealed class Reader
        {
            readonly string _s;
            public int Pos;

            public Reader(string s) { _s = s; }

            public InvalidDataException Error(string what) => new InvalidDataException($"JSON: {what} at {Pos}");

            public void SkipSpace()
            {
                while (Pos < _s.Length && (_s[Pos] == ' ' || _s[Pos] == '\t' || _s[Pos] == '\n' || _s[Pos] == '\r')) Pos++;
            }

            public object Value()
            {
                if (Pos >= _s.Length) throw Error("unexpected end");
                char c = _s[Pos];
                switch (c)
                {
                    case '{': return Object();
                    case '[': return Array();
                    case '"': return String();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default: return Number();
                }
            }

            void Expect(string word)
            {
                if (string.CompareOrdinal(_s, Pos, word, 0, word.Length) != 0) throw Error("bad literal");
                Pos += word.Length;
            }

            Dictionary<string, object> Object()
            {
                var obj = new Dictionary<string, object>(StringComparer.Ordinal);
                Pos++;
                SkipSpace();
                if (Pos < _s.Length && _s[Pos] == '}') { Pos++; return obj; }
                while (true)
                {
                    SkipSpace();
                    if (Pos >= _s.Length || _s[Pos] != '"') throw Error("expected a key");
                    string key = String();
                    SkipSpace();
                    if (Pos >= _s.Length || _s[Pos] != ':') throw Error("expected ':'");
                    Pos++;
                    SkipSpace();
                    obj[key] = Value();
                    SkipSpace();
                    if (Pos >= _s.Length) throw Error("unexpected end");
                    if (_s[Pos] == ',') { Pos++; continue; }
                    if (_s[Pos] == '}') { Pos++; return obj; }
                    throw Error("expected ',' or '}'");
                }
            }

            List<object> Array()
            {
                var list = new List<object>();
                Pos++;
                SkipSpace();
                if (Pos < _s.Length && _s[Pos] == ']') { Pos++; return list; }
                while (true)
                {
                    SkipSpace();
                    list.Add(Value());
                    SkipSpace();
                    if (Pos >= _s.Length) throw Error("unexpected end");
                    if (_s[Pos] == ',') { Pos++; continue; }
                    if (_s[Pos] == ']') { Pos++; return list; }
                    throw Error("expected ',' or ']'");
                }
            }

            string String()
            {
                Pos++;
                int start = Pos;
                // Fast path: no escapes.
                while (Pos < _s.Length && _s[Pos] != '"' && _s[Pos] != '\\') Pos++;
                if (Pos < _s.Length && _s[Pos] == '"') { Pos++; return _s.Substring(start, Pos - start - 1); }
                var sb = new StringBuilder(_s, start, Pos - start, Pos - start + 16);
                while (true)
                {
                    if (Pos >= _s.Length) throw Error("unterminated string");
                    char c = _s[Pos++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (Pos >= _s.Length) throw Error("unterminated string");
                    char e = _s[Pos++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (Pos + 4 > _s.Length) throw Error("bad \\u escape");
                            sb.Append((char)int.Parse(_s.Substring(Pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            Pos += 4;
                            break;
                        default: throw Error("bad escape");
                    }
                }
            }

            object Number()
            {
                int start = Pos;
                bool real = false;
                while (Pos < _s.Length)
                {
                    char c = _s[Pos];
                    if (c >= '0' && c <= '9' || c == '-' || c == '+') { Pos++; continue; }
                    if (c == '.' || c == 'e' || c == 'E') { real = true; Pos++; continue; }
                    break;
                }
                if (Pos == start) throw Error("unexpected character");
                string token = _s.Substring(start, Pos - start);
                if (!real && long.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l)) return l;
                return double.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }

        public static void WriteString(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (char c in value ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
