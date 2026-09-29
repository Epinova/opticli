using System.Text;

namespace OptiCli.Agent.Safety;

/// <summary>
/// Splits a SQL Server connection string into key/value pairs exactly the way SqlClient does
/// (<c>DbConnectionOptions.GetKeyValuePair</c>, non-ODBC rules): quoted values may contain
/// <c>;</c>, doubled quotes escape, <c>==</c> in a key is a literal <c>=</c>, keys are lower-cased
/// and whitespace-trimmed, unquoted values are trimmed.
/// </summary>
/// <remarks>
/// The agent can't compile against the host's Microsoft.Data.SqlClient without pinning a version,
/// so it carries this port. Getting it wrong in the lenient direction would let a crafted string
/// (<c>Server=remote;Password="x;Server=localhost"</c>) pass the local-only check, which is why the
/// tests compare it with SqlClient's own parser on every case.
/// </remarks>
internal static class ConnectionStringParser
{
    private enum State
    {
        NothingYet,
        Key,
        KeyEqual,
        KeyEnd,
        UnquotedValue,
        DoubleQuoteValue,
        DoubleQuoteValueQuote,
        SingleQuoteValue,
        SingleQuoteValueQuote,
        QuotedValueEnd,
        NullTermination,
    }

    /// <summary>All pairs in order, duplicates included. A key without a value maps to null.</summary>
    /// <exception cref="FormatException">The string is not syntactically valid.</exception>
    public static IReadOnlyList<KeyValuePair<string, string?>> Parse(string connectionString)
    {
        var pairs = new List<KeyValuePair<string, string?>>();
        var buffer = new StringBuilder();
        var position = 0;
        while (position < connectionString.Length)
        {
            position = ReadPair(connectionString, position, buffer, out var key, out var value);
            if (string.IsNullOrEmpty(key))
            {
                break;
            }
            pairs.Add(new(key, value));
        }
        return pairs;
    }

    private static int ReadPair(string s, int position, StringBuilder buffer, out string? key, out string? value)
    {
        var start = position;
        buffer.Clear();
        key = null;
        value = null;
        var c = '\0';
        var state = State.NothingYet;

        for (; position < s.Length; position++)
        {
            c = s[position];
            var append = true;
            var stop = false;

            // A few states hand the current character on to the next state (SqlClient's "goto case").
            while (true)
            {
                switch (state)
                {
                    case State.NothingYet:
                        if (c == ';' || char.IsWhiteSpace(c))
                        {
                            append = false;
                        }
                        else if (c == '\0')
                        {
                            state = State.NullTermination;
                            append = false;
                        }
                        else if (char.IsControl(c))
                        {
                            throw Syntax(start);
                        }
                        else
                        {
                            start = position;
                            if (c == '=')
                            {
                                state = State.KeyEqual;
                                append = false;
                            }
                            else
                            {
                                state = State.Key;
                            }
                        }
                        break;

                    case State.Key:
                        if (c == '=')
                        {
                            state = State.KeyEqual;
                            append = false;
                        }
                        else if (!char.IsWhiteSpace(c) && char.IsControl(c))
                        {
                            throw Syntax(start);
                        }
                        break;

                    case State.KeyEqual:
                        if (c == '=')
                        {
                            // "==" is an escaped '=' inside the key.
                            state = State.Key;
                            break;
                        }
                        key = KeyName(buffer);
                        if (string.IsNullOrEmpty(key))
                        {
                            throw Syntax(start);
                        }
                        buffer.Clear();
                        state = State.KeyEnd;
                        continue;

                    case State.KeyEnd:
                        if (char.IsWhiteSpace(c))
                        {
                            append = false;
                        }
                        else if (c == '\'')
                        {
                            state = State.SingleQuoteValue;
                            append = false;
                        }
                        else if (c == '"')
                        {
                            state = State.DoubleQuoteValue;
                            append = false;
                        }
                        else if (c == ';' || c == '\0')
                        {
                            stop = true;
                        }
                        else if (char.IsControl(c))
                        {
                            throw Syntax(start);
                        }
                        else
                        {
                            state = State.UnquotedValue;
                        }
                        break;

                    case State.UnquotedValue:
                        if (!char.IsWhiteSpace(c) && (char.IsControl(c) || c == ';'))
                        {
                            stop = true;
                        }
                        break;

                    case State.DoubleQuoteValue:
                    case State.SingleQuoteValue:
                        var quote = state == State.DoubleQuoteValue ? '"' : '\'';
                        if (c == quote)
                        {
                            state = state == State.DoubleQuoteValue ? State.DoubleQuoteValueQuote : State.SingleQuoteValueQuote;
                            append = false;
                        }
                        else if (c == '\0')
                        {
                            throw Syntax(start);
                        }
                        break;

                    case State.DoubleQuoteValueQuote:
                    case State.SingleQuoteValueQuote:
                        var closing = state == State.DoubleQuoteValueQuote ? '"' : '\'';
                        if (c == closing)
                        {
                            // Doubled quote: a literal quote character inside the value.
                            state = state == State.DoubleQuoteValueQuote ? State.DoubleQuoteValue : State.SingleQuoteValue;
                            break;
                        }
                        value = buffer.ToString();
                        state = State.QuotedValueEnd;
                        continue;

                    case State.QuotedValueEnd:
                        append = false;
                        if (char.IsWhiteSpace(c))
                        {
                            break;
                        }
                        if (c == ';')
                        {
                            stop = true;
                        }
                        else if (c == '\0')
                        {
                            state = State.NullTermination;
                        }
                        else
                        {
                            throw Syntax(start);
                        }
                        break;

                    case State.NullTermination:
                        if (c != '\0' && !char.IsWhiteSpace(c))
                        {
                            throw Syntax(position);
                        }
                        append = false;
                        break;
                }
                break;
            }

            if (stop)
            {
                break;
            }
            if (append)
            {
                buffer.Append(c);
            }
        }

        switch (state)
        {
            case State.Key:
            case State.DoubleQuoteValue:
            case State.SingleQuoteValue:
                throw Syntax(start);
            case State.KeyEqual:
                key = KeyName(buffer);
                if (string.IsNullOrEmpty(key))
                {
                    throw Syntax(start);
                }
                break;
            case State.UnquotedValue:
                value = buffer.ToString().Trim();
                if (value[^1] is '\'' or '"')
                {
                    throw Syntax(start);
                }
                break;
            case State.DoubleQuoteValueQuote:
            case State.SingleQuoteValueQuote:
            case State.QuotedValueEnd:
                value = buffer.ToString();
                break;
        }

        if (c == ';' && position < s.Length)
        {
            position++;
        }
        return position;
    }

    private static string KeyName(StringBuilder buffer) => buffer.ToString().TrimEnd().ToLowerInvariant();

    private static FormatException Syntax(int index) =>
        new($"Format of the connection string is invalid, starting at index {index}.");
}
