// <copyright file="JsonParser.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Modeling.Json
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;

    /// <summary>
    /// A small JSON parser that records the location of every value.
    /// </summary>
    /// <remarks>
    /// Written in-house for two reasons.  First, every model diagnostic must point at an exact line and column, and
    /// System.Text.Json's document model does not expose positions.  Second, code loaded by the compiler inside Visual Studio
    /// should carry no third-party dependencies.  The parser accepts <c>//</c> and <c>/* */</c> comments and trailing commas,
    /// because model files are edited by hand.
    /// </remarks>
    public sealed class JsonParser
    {
        /// <summary>
        /// The file path, used in locations.
        /// </summary>
        private readonly string path;

        /// <summary>
        /// The text being parsed.
        /// </summary>
        private readonly string text;

        /// <summary>
        /// The character offset at which each line starts.
        /// </summary>
        private readonly List<int> lineStarts = new List<int>();

        /// <summary>
        /// The current position.
        /// </summary>
        private int position;

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonParser"/> class.
        /// </summary>
        /// <param name="path">The file path, used in locations.</param>
        /// <param name="text">The text to parse.</param>
        public JsonParser(string path, string text)
        {
            this.path = path;
            this.text = text;
            this.lineStarts.Add(0);
            for (var index = 0; index < text.Length; index++)
            {
                if (text[index] == '\n')
                {
                    this.lineStarts.Add(index + 1);
                }
            }
        }

        /// <summary>
        /// Parses the text.
        /// </summary>
        /// <returns>The root value.</returns>
        /// <exception cref="JsonSyntaxException">The text is not valid JSON.</exception>
        public JsonNode Parse()
        {
            this.SkipTrivia();
            var value = this.ParseValue();
            this.SkipTrivia();
            if (this.position < this.text.Length)
            {
                throw this.Error("Unexpected text after the end of the document.", this.position, 1);
            }

            return value;
        }

        /// <summary>
        /// Computes the location of a span of text.
        /// </summary>
        /// <param name="start">The starting offset.</param>
        /// <param name="length">The length.</param>
        /// <returns>The location.</returns>
        public SourceLocation LocationOf(int start, int length)
        {
            var end = start + length;
            var (startLine, startColumn) = this.LineAndColumn(start);
            var (endLine, endColumn) = this.LineAndColumn(end);
            return new SourceLocation(this.path, start, length, startLine, startColumn, endLine, endColumn);
        }

        /// <summary>
        /// Converts an offset to a zero-based line and column.
        /// </summary>
        /// <param name="offset">The character offset.</param>
        /// <returns>The line and column.</returns>
        private (int Line, int Column) LineAndColumn(int offset)
        {
            var index = this.lineStarts.BinarySearch(offset);
            var line = index >= 0 ? index : ~index - 1;
            return (line, offset - this.lineStarts[line]);
        }

        /// <summary>
        /// Parses any value at the current position.
        /// </summary>
        /// <returns>The value.</returns>
        private JsonNode ParseValue()
        {
            if (this.position >= this.text.Length)
            {
                throw this.Error("Unexpected end of file; expected a value.", this.position, 0);
            }

            var character = this.text[this.position];
            switch (character)
            {
                case '{':
                    return this.ParseObject();
                case '[':
                    return this.ParseArray();
                case '"':
                    return this.ParseString();
                case 't':
                    return this.ParseLiteral("true", location => new JsonBoolean(location, true));
                case 'f':
                    return this.ParseLiteral("false", location => new JsonBoolean(location, false));
                case 'n':
                    return this.ParseLiteral("null", location => new JsonNull(location));
                default:
                    if (character == '-' || (character >= '0' && character <= '9'))
                    {
                        return this.ParseNumber();
                    }

                    throw this.Error($"Unexpected character '{character}'.", this.position, 1);
            }
        }

        /// <summary>
        /// Parses an object.
        /// </summary>
        /// <returns>The object.</returns>
        private JsonObject ParseObject()
        {
            var start = this.position;
            this.position++;
            var properties = new List<JsonProperty>();
            while (true)
            {
                this.SkipTrivia();
                if (this.Peek() == '}')
                {
                    this.position++;
                    return new JsonObject(this.LocationOf(start, this.position - start), properties);
                }

                if (this.Peek() != '"')
                {
                    throw this.Error("Expected a property name in double quotes.", this.position, 1);
                }

                var name = this.ParseString();
                this.SkipTrivia();
                if (this.Peek() != ':')
                {
                    throw this.Error("Expected ':' after the property name.", this.position, 1);
                }

                this.position++;
                this.SkipTrivia();
                var value = this.ParseValue();
                properties.Add(new JsonProperty(name.Value, name.Location, value));
                this.SkipTrivia();
                var next = this.Peek();
                if (next == ',')
                {
                    this.position++;
                }
                else if (next != '}')
                {
                    throw this.Error("Expected ',' or '}'.", this.position, 1);
                }
            }
        }

        /// <summary>
        /// Parses an array.
        /// </summary>
        /// <returns>The array.</returns>
        private JsonArray ParseArray()
        {
            var start = this.position;
            this.position++;
            var items = new List<JsonNode>();
            while (true)
            {
                this.SkipTrivia();
                if (this.Peek() == ']')
                {
                    this.position++;
                    return new JsonArray(this.LocationOf(start, this.position - start), items);
                }

                items.Add(this.ParseValue());
                this.SkipTrivia();
                var next = this.Peek();
                if (next == ',')
                {
                    this.position++;
                }
                else if (next != ']')
                {
                    throw this.Error("Expected ',' or ']'.", this.position, 1);
                }
            }
        }

        /// <summary>
        /// Parses a string, including its escapes.
        /// </summary>
        /// <returns>The string.</returns>
        private JsonString ParseString()
        {
            var start = this.position;
            this.position++;
            var builder = new StringBuilder();
            while (true)
            {
                if (this.position >= this.text.Length)
                {
                    throw this.Error("Unterminated string.", start, this.position - start);
                }

                var character = this.text[this.position++];
                if (character == '"')
                {
                    return new JsonString(this.LocationOf(start, this.position - start), builder.ToString());
                }

                if (character == '\n')
                {
                    throw this.Error("Unterminated string.", start, this.position - start - 1);
                }

                if (character != '\\')
                {
                    builder.Append(character);
                    continue;
                }

                if (this.position >= this.text.Length)
                {
                    throw this.Error("Unterminated escape sequence.", this.position - 1, 1);
                }

                var escape = this.text[this.position++];
                switch (escape)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        if (this.position + 4 > this.text.Length ||
                            !int.TryParse(this.text.Substring(this.position, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                        {
                            throw this.Error("Invalid \\u escape; expected four hexadecimal digits.", this.position - 2, 2);
                        }

                        builder.Append((char)code);
                        this.position += 4;
                        break;
                    default:
                        throw this.Error($"Invalid escape sequence '\\{escape}'.", this.position - 2, 2);
                }
            }
        }

        /// <summary>
        /// Parses a number following the JSON grammar.
        /// </summary>
        /// <returns>The number.</returns>
        private JsonNumber ParseNumber()
        {
            var start = this.position;
            if (this.Peek() == '-')
            {
                this.position++;
            }

            if (!this.TryReadDigits())
            {
                throw this.Error("Invalid number.", start, Math.Max(1, this.position - start));
            }

            if (this.Peek() == '.')
            {
                this.position++;
                if (!this.TryReadDigits())
                {
                    throw this.Error("Invalid number; expected digits after the decimal point.", start, this.position - start);
                }
            }

            if (this.Peek() == 'e' || this.Peek() == 'E')
            {
                this.position++;
                if (this.Peek() == '+' || this.Peek() == '-')
                {
                    this.position++;
                }

                if (!this.TryReadDigits())
                {
                    throw this.Error("Invalid number; expected digits in the exponent.", start, this.position - start);
                }
            }

            return new JsonNumber(this.LocationOf(start, this.position - start), this.text.Substring(start, this.position - start));
        }

        /// <summary>
        /// Parses a literal keyword.
        /// </summary>
        /// <param name="keyword">The keyword.</param>
        /// <param name="create">Creates the node from its location.</param>
        /// <returns>The node.</returns>
        private JsonNode ParseLiteral(string keyword, Func<SourceLocation, JsonNode> create)
        {
            if (string.CompareOrdinal(this.text, this.position, keyword, 0, keyword.Length) != 0)
            {
                throw this.Error($"Unexpected character '{this.text[this.position]}'.", this.position, 1);
            }

            var location = this.LocationOf(this.position, keyword.Length);
            this.position += keyword.Length;
            return create(location);
        }

        /// <summary>
        /// Reads one or more digits.
        /// </summary>
        /// <returns><c>true</c> if at least one digit was read.</returns>
        private bool TryReadDigits()
        {
            var start = this.position;
            while (this.position < this.text.Length && this.text[this.position] >= '0' && this.text[this.position] <= '9')
            {
                this.position++;
            }

            return this.position > start;
        }

        /// <summary>
        /// Skips whitespace and comments.
        /// </summary>
        private void SkipTrivia()
        {
            while (this.position < this.text.Length)
            {
                var character = this.text[this.position];
                if (character == ' ' || character == '\t' || character == '\r' || character == '\n' || character == '\uFEFF')
                {
                    this.position++;
                }
                else if (character == '/' && this.position + 1 < this.text.Length && this.text[this.position + 1] == '/')
                {
                    while (this.position < this.text.Length && this.text[this.position] != '\n')
                    {
                        this.position++;
                    }
                }
                else if (character == '/' && this.position + 1 < this.text.Length && this.text[this.position + 1] == '*')
                {
                    var end = this.text.IndexOf("*/", this.position + 2, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        throw this.Error("Unterminated comment.", this.position, 2);
                    }

                    this.position = end + 2;
                }
                else
                {
                    return;
                }
            }
        }

        /// <summary>
        /// Gets the character at the current position.
        /// </summary>
        /// <returns>The character, or <c>'\0'</c> at the end of the text.</returns>
        private char Peek() => this.position < this.text.Length ? this.text[this.position] : '\0';

        /// <summary>
        /// Creates a syntax exception at a span of text.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <param name="start">The starting offset.</param>
        /// <param name="length">The length.</param>
        /// <returns>The exception.</returns>
        private JsonSyntaxException Error(string message, int start, int length) =>
            new JsonSyntaxException(message, this.LocationOf(start, Math.Min(length, Math.Max(0, this.text.Length - start))));
    }

    /// <summary>
    /// Thrown when a model file is not valid JSON.
    /// </summary>
    public sealed class JsonSyntaxException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="JsonSyntaxException"/> class.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <param name="location">Where the error is.</param>
        public JsonSyntaxException(string message, SourceLocation location)
            : base(message)
        {
            this.Location = location;
        }

        /// <summary>
        /// Gets where the error is.
        /// </summary>
        public SourceLocation Location { get; }
    }
}
