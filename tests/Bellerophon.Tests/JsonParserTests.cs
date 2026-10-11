// <copyright file="JsonParserTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Tests
{
    using Bellerophon.Modeling.Json;
    using Xunit;

    /// <summary>
    /// Tests for the location-tracking JSON parser.
    /// </summary>
    public class JsonParserTests
    {
        /// <summary>
        /// Hand-edited model files may contain comments and trailing commas.
        /// </summary>
        [Fact]
        public void CommentsAndTrailingCommasAreAccepted()
        {
            const string Text = """
                {
                  // A line comment.
                  "a": [1, 2, 3,],
                  /* A block comment. */
                  "b": { "c": "d", },
                }
                """;

            var root = Assert.IsType<JsonObject>(new JsonParser("test.json", Text).Parse());

            var array = Assert.IsType<JsonArray>(root.Find("a")!.Value);
            Assert.Equal(3, array.Items.Count);
            var nested = Assert.IsType<JsonObject>(root.Find("b")!.Value);
            Assert.Equal("d", Assert.IsType<JsonString>(nested.Find("c")!.Value).Value);
        }

        /// <summary>
        /// Every value knows its line and column, so diagnostics can point at it.
        /// </summary>
        [Fact]
        public void ValuesRecordTheirLocation()
        {
            const string Text = "{\n  \"name\": \"value\"\n}";

            var root = Assert.IsType<JsonObject>(new JsonParser("test.json", Text).Parse());

            var property = root.Find("name")!;
            Assert.Equal(1, property.NameLocation.StartLine);
            Assert.Equal(2, property.NameLocation.StartColumn);
            Assert.Equal(1, property.Value.Location.StartLine);
            Assert.Equal(10, property.Value.Location.StartColumn);
        }

        /// <summary>
        /// A syntax error reports where it is.
        /// </summary>
        [Fact]
        public void SyntaxErrorReportsLineAndColumn()
        {
            const string Text = "{\n  \"a\": 1\n  \"b\": 2\n}";

            var exception = Assert.Throws<JsonSyntaxException>(() => new JsonParser("test.json", Text).Parse());

            Assert.Equal(2, exception.Location.StartLine);
            Assert.Equal(2, exception.Location.StartColumn);
            Assert.Contains("Expected ','", exception.Message);
        }
    }
}
