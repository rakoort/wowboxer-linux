using System.Globalization;
using System.Text;

namespace WoWBoxer.Layouts.Linux;

/// <summary>
/// A Lua table as WoW writes SavedVariables: keys are strings or numbers (<c>["key"] =</c>, <c>[1] =</c>, <c>key =</c>,
/// or none for list items, which get 1, 2, 3 …), values are strings, numbers, booleans, nil or tables.
/// Number keys are stored as <see cref="double"/>.
/// </summary>
public sealed class LuaTable
{
    private readonly Dictionary<object, object?> _entries = [];

    public IReadOnlyDictionary<object, object?> Entries => _entries;

    /// <summary>The value of a string key (null if missing or nil).</summary>
    public object? this[string key] => _entries.GetValueOrDefault(key);

    /// <summary>The list part: values of the keys 1, 2, 3 … in order, up to the first gap.</summary>
    public IReadOnlyList<object?> Items
    {
        get
        {
            var list = new List<object?>();
            for (var i = 1; _entries.TryGetValue((double)i, out var v); i++) list.Add(v);
            return list;
        }
    }

    public string? String(string key) => this[key] as string;

    public double? Number(string key) => this[key] as double?;

    public LuaTable? Table(string key) => this[key] as LuaTable;

    internal void Set(object key, object? value) => _entries[key] = value;
}

/// <summary>
/// A small, read-only parser for WoW's SavedVariables files (<c>Name = { … }</c> assignments). It never runs Lua: it
/// only reads constructors of literals – strings (all Lua escapes, long brackets), numbers (decimal, hex, exponent),
/// booleans, nil and nested tables – and skips comments. CRLF and LF both work.
/// </summary>
public static class LuaReader
{
    /// <summary>The top-level assignments of a file (variable → value). Throws <see cref="FormatException"/> on anything else.</summary>
    public static IReadOnlyDictionary<string, object?> ReadAssignments(string text)
    {
        var p = new Parser(text);
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        while (true)
        {
            p.SkipSpace();
            if (p.AtEnd) return result;
            var name = p.Name();
            p.SkipSpace();
            p.Expect('=');
            result[name] = p.Value();
            p.SkipSpace();
            p.TryTake(';');
        }
    }

    private ref struct Parser(string text)
    {
        private readonly string _s = text;
        private int _i;

        public readonly bool AtEnd => _i >= _s.Length;

        private readonly char Peek(int ahead = 0) => _i + ahead < _s.Length ? _s[_i + ahead] : '\0';

        private readonly FormatException Error(string what)
        {
            var line = 1 + _s.AsSpan(0, Math.Min(_i, _s.Length)).Count('\n');
            return new FormatException($"Lua: {what} at line {line}.");
        }

        public void Expect(char c)
        {
            if (Peek() != c) throw Error($"'{c}' expected");
            _i++;
        }

        public bool TryTake(char c)
        {
            if (Peek() != c) return false;
            _i++;
            return true;
        }

        public void SkipSpace()
        {
            while (!AtEnd)
            {
                var c = Peek();
                if (char.IsWhiteSpace(c) || c == '\uFEFF') _i++;
                else if (c == '-' && Peek(1) == '-')
                {
                    _i += 2;
                    if (Peek() == '[' && LongBracketLevel() is { } level) LongString(level);
                    else
                        while (!AtEnd && Peek() != '\n') _i++;
                }
                else return;
            }
        }

        public string Name()
        {
            var start = _i;
            if (!(char.IsAsciiLetter(Peek()) || Peek() == '_')) throw Error("name expected");
            while (char.IsAsciiLetterOrDigit(Peek()) || Peek() == '_') _i++;
            return _s[start.._i];
        }

        public object? Value()
        {
            SkipSpace();
            var c = Peek();
            if (c == '{') return Table();
            if (c is '"' or '\'') return QuotedString();
            if (c == '[' && LongBracketLevel() is { } level) return LongString(level);
            if (c == '-' || c == '.' || char.IsAsciiDigit(c)) return Number();
            if (char.IsAsciiLetter(c))
            {
                var word = Name();
                return word switch
                {
                    "true" => true,
                    "false" => false,
                    "nil" => null,
                    "inf" => double.PositiveInfinity,   // not Lua, but some SavedVariables writers produce it
                    _ => throw Error($"unexpected \"{word}\""),
                };
            }
            throw Error($"unexpected '{c}'");
        }

        private LuaTable Table()
        {
            Expect('{');
            var table = new LuaTable();
            var next = 1;
            while (true)
            {
                SkipSpace();
                if (TryTake('}')) return table;
                object key;
                if (Peek() == '[' && LongBracketLevel() is null)
                {
                    _i++;
                    key = Value() ?? throw Error("nil table key");
                    SkipSpace();
                    Expect(']');
                    SkipSpace();
                    Expect('=');
                    table.Set(key, Value());
                }
                else if ((char.IsAsciiLetter(Peek()) || Peek() == '_') && IsNameAssignment())
                {
                    key = Name();
                    SkipSpace();
                    Expect('=');
                    table.Set(key, Value());
                }
                else
                    table.Set((double)next++, Value());
                SkipSpace();
                if (!TryTake(',') && !TryTake(';'))
                {
                    SkipSpace();
                    Expect('}');
                    return table;
                }
            }
        }

        /// <summary>True if a name here is followed by '=' (a field), not a value like <c>true</c>.</summary>
        private readonly bool IsNameAssignment()
        {
            var j = _i;
            while (j < _s.Length && (char.IsAsciiLetterOrDigit(_s[j]) || _s[j] == '_')) j++;
            while (j < _s.Length && char.IsWhiteSpace(_s[j])) j++;
            return j < _s.Length && _s[j] == '=' && (j + 1 >= _s.Length || _s[j + 1] != '=');
        }

        private double Number()
        {
            var start = _i;
            var negative = TryTake('-');
            if (Peek() == '0' && Peek(1) is 'x' or 'X')
            {
                _i += 2;
                var hexStart = _i;
                while (char.IsAsciiHexDigit(Peek())) _i++;
                if (_i == hexStart) throw Error("hex digits expected");
                var hex = (double)ulong.Parse(_s.AsSpan(hexStart, _i - hexStart), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return negative ? -hex : hex;
            }
            while (char.IsAsciiDigit(Peek()) || Peek() == '.') _i++;
            if (Peek() is 'e' or 'E')
            {
                _i++;
                if (Peek() is '+' or '-') _i++;
                while (char.IsAsciiDigit(Peek())) _i++;
            }
            if (!double.TryParse(_s.AsSpan(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                throw Error("bad number");
            return d;
        }

        private string QuotedString()
        {
            var quote = Peek();
            _i++;
            var sb = new StringBuilder();
            var bytes = new List<byte>();   // byte escapes in a row: decoded together as UTF-8
            while (true)
            {
                if (AtEnd) throw Error("unfinished string");
                var c = _s[_i++];
                if (!(c == '\\' && (Peek() == 'x' || char.IsAsciiDigit(Peek())))) Flush(sb, bytes);
                if (c == quote) return sb.ToString();
                if (c is '\n' or '\r') throw Error("line break in string");
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }
                if (AtEnd) throw Error("unfinished string");
                var e = _s[_i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'a': sb.Append('\a'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'v': sb.Append('\v'); break;
                    case '\\' or '"' or '\'': sb.Append(e); break;
                    case '\n':
                        sb.Append('\n');
                        TryTake('\r');
                        break;
                    case '\r':
                        sb.Append('\n');
                        TryTake('\n');
                        break;
                    case 'z':
                        while (!AtEnd && char.IsWhiteSpace(Peek())) _i++;
                        break;
                    case 'x':
                        if (!char.IsAsciiHexDigit(Peek()) || !char.IsAsciiHexDigit(Peek(1))) throw Error("bad \\x escape");
                        bytes.Add(Convert.ToByte(_s.Substring(_i, 2), 16));
                        _i += 2;
                        break;
                    case 'u':
                        Expect('{');
                        var hexStart = _i;
                        while (char.IsAsciiHexDigit(Peek())) _i++;
                        var code = int.Parse(_s.AsSpan(hexStart, _i - hexStart), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        Expect('}');
                        sb.Append(char.ConvertFromUtf32(code));
                        break;
                    case >= '0' and <= '9':
                        var value = e - '0';
                        for (var k = 0; k < 2 && char.IsAsciiDigit(Peek()); k++) value = value * 10 + (_s[_i++] - '0');
                        if (value > 255) throw Error("bad decimal escape");
                        bytes.Add((byte)value);
                        break;
                    default: throw Error($"bad escape \\{e}");
                }
            }
        }

        /// <summary>Lua strings are bytes and WoW writes UTF-8: escaped bytes are decoded as UTF-8 (invalid ones become U+FFFD).</summary>
        private static void Flush(StringBuilder sb, List<byte> bytes)
        {
            if (bytes.Count == 0) return;
            sb.Append(Encoding.UTF8.GetString(bytes.ToArray()));
            bytes.Clear();
        }

        /// <summary>The level of a long bracket <c>[==[</c> starting here, or null if this is no long bracket.</summary>
        private readonly int? LongBracketLevel()
        {
            if (Peek() != '[') return null;
            var level = 0;
            while (Peek(1 + level) == '=') level++;
            return Peek(1 + level) == '[' ? level : null;
        }

        private string LongString(int level)
        {
            _i += level + 2;
            if (Peek() == '\r') _i++;   // a line break right after the opening bracket is skipped
            if (Peek() == '\n') _i++;
            var close = "]" + new string('=', level) + "]";
            var end = _s.IndexOf(close, _i, StringComparison.Ordinal);
            if (end < 0) throw Error("unfinished long string");
            var value = _s[_i..end].Replace("\r\n", "\n");
            _i = end + close.Length;
            return value;
        }
    }
}
