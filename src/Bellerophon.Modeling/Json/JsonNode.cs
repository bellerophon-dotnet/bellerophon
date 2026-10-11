// <copyright file="JsonNode.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Modeling.Json
{
    using System.Collections.Generic;
    using System.Globalization;

    /// <summary>
    /// A parsed JSON value that remembers where it came from, so every diagnostic can point at the exact text.
    /// </summary>
    public abstract class JsonNode
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="JsonNode"/> class.
        /// </summary>
        /// <param name="location">The location of the value in the file.</param>
        protected JsonNode(SourceLocation location)
        {
            this.Location = location;
        }

        /// <summary>
        /// Gets the location of the value in the file.
        /// </summary>
        public SourceLocation Location { get; }

        /// <summary>
        /// Gets a short description of the JSON type, for diagnostics.
        /// </summary>
        public abstract string Kind { get; }
    }

    /// <summary>
    /// A JSON object.  Properties keep their file order and duplicates are preserved, so the model builder can report them.
    /// </summary>
    public sealed class JsonObject : JsonNode
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="JsonObject"/> class.
        /// </summary>
        /// <param name="location">The location of the object.</param>
        /// <param name="properties">The properties, in file order.</param>
        public JsonObject(SourceLocation location, IReadOnlyList<JsonProperty> properties)
            : base(location)
        {
            this.Properties = properties;
        }

        /// <inheritdoc/>
        public override string Kind => "an object";

        /// <summary>
        /// Gets the properties, in file order.
        /// </summary>
        public IReadOnlyList<JsonProperty> Properties { get; }

        /// <summary>
        /// Finds the first property with the given name.
        /// </summary>
        /// <param name="name">The property name (case-sensitive).</param>
        /// <returns>The property, or <c>null</c>.</returns>
        public JsonProperty? Find(string name)
        {
            foreach (var property in this.Properties)
            {
                if (property.Name == name)
                {
                    return property;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// A property of a JSON object.
    /// </summary>
    public sealed class JsonProperty
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="JsonProperty"/> class.
        /// </summary>
        /// <param name="name">The property name.</param>
        /// <param name="nameLocation">The location of the name.</param>
        /// <param name="value">The value.</param>
        public JsonProperty(string name, SourceLocation nameLocation, JsonNode value)
        {
            this.Name = name;
            this.NameLocation = nameLocation;
            this.Value = value;
        }

        /// <summary>
        /// Gets the property name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the location of the name.
        /// </summary>
        public SourceLocation NameLocation { get; }

        /// <summary>
        /// Gets the value.
        /// </summary>
        public JsonNode Value { get; }
    }

    /// <summary>
    /// A JSON array.
    /// </summary>
    public sealed class JsonArray : JsonNode
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="JsonArray"/> class.
        /// </summary>
        /// <param name="location">The location of the array.</param>
        /// <param name="items">The items.</param>
        public JsonArray(SourceLocation location, IReadOnlyList<JsonNode> items)
            : base(location)
        {
            this.Items = items;
        }

        /// <inheritdoc/>
        public override string Kind => "an array";

        /// <summary>
        /// Gets the items.
        /// </summary>
        public IReadOnlyList<JsonNode> Items { get; }
    }

    /// <summary>
    /// A JSON string.
    /// </summary>
    public sealed class JsonString : JsonNode
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="JsonString"/> class.
        /// </summary>
        /// <param name="location">The location of the string, including its quotes.</param>
        /// <param name="value">The unescaped value.</param>
        public JsonString(SourceLocation location, string value)
            : base(location)
        {
            this.Value = value;
        }

        /// <inheritdoc/>
        public override string Kind => "a string";

        /// <summary>
        /// Gets the unescaped value.
        /// </summary>
        public string Value { get; }
    }

    /// <summary>
    /// A JSON number, kept as text so no precision is lost before the model decides what the number means.
    /// </summary>
    public sealed class JsonNumber : JsonNode
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="JsonNumber"/> class.
        /// </summary>
        /// <param name="location">The location of the number.</param>
        /// <param name="text">The number exactly as written.</param>
        public JsonNumber(SourceLocation location, string text)
            : base(location)
        {
            this.Text = text;
        }

        /// <inheritdoc/>
        public override string Kind => "a number";

        /// <summary>
        /// Gets the number exactly as written.
        /// </summary>
        public string Text { get; }

        /// <summary>
        /// Attempts to read the number as a 32-bit integer.
        /// </summary>
        /// <param name="value">The integer.</param>
        /// <returns><c>true</c> if the number is an integer in range.</returns>
        public bool TryGetInt32(out int value) =>
            int.TryParse(this.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// A JSON <c>true</c> or <c>false</c>.
    /// </summary>
    public sealed class JsonBoolean : JsonNode
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="JsonBoolean"/> class.
        /// </summary>
        /// <param name="location">The location of the literal.</param>
        /// <param name="value">The value.</param>
        public JsonBoolean(SourceLocation location, bool value)
            : base(location)
        {
            this.Value = value;
        }

        /// <inheritdoc/>
        public override string Kind => "a boolean";

        /// <summary>
        /// Gets the value.
        /// </summary>
        public bool Value { get; }
    }

    /// <summary>
    /// A JSON <c>null</c>.
    /// </summary>
    public sealed class JsonNull : JsonNode
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="JsonNull"/> class.
        /// </summary>
        /// <param name="location">The location of the literal.</param>
        public JsonNull(SourceLocation location)
            : base(location)
        {
        }

        /// <inheritdoc/>
        public override string Kind => "null";
    }
}
