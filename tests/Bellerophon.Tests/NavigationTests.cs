// <copyright file="NavigationTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Tests
{
    using System.Linq;
    using Bellerophon.Modeling;
    using Bellerophon.Modeling.Diagnostics;
    using Xunit;

    /// <summary>
    /// Tests for plurals, the plural override, and the names of navigation properties.
    /// </summary>
    public class NavigationTests
    {
        /// <summary>
        /// Regular English plurals, applied to the last word of a PascalCase name.
        /// </summary>
        [Theory]
        [InlineData("Account", "Accounts")]
        [InlineData("Address", "Addresses")]
        [InlineData("Status", "Statuses")]
        [InlineData("Box", "Boxes")]
        [InlineData("Branch", "Branches")]
        [InlineData("Entity", "Entities")]
        [InlineData("Day", "Days")]
        [InlineData("OrderIntent", "OrderIntents")]
        public void RegularPlurals(string name, string plural)
        {
            Assert.Equal(plural, Navigation.Plural(name));
        }

        /// <summary>
        /// A table's plural can be overridden for irregular words.
        /// </summary>
        [Fact]
        public void PluralOverrideIsUsed()
        {
            var text = Fixture.Read("bellerophon.json").Replace("\"key\": [\"PersonId\"],\n      \"references\": {\n        \"Node\"", "\"key\": [\"PersonId\"],\n      \"plural\": \"People\",\n      \"references\": {\n        \"Node\"");

            var model = ModelLoader.Load("bellerophon.json", text).Model!;

            Assert.Equal("People", model.FindTable("Person")!.Plural);
            Assert.Equal("Accounts", model.FindTable("Account")!.Plural);
        }

        /// <summary>
        /// One-to-one extensions use the singular; other relations the plural; a reference whose name differs from its
        /// table is decorated.
        /// </summary>
        [Fact]
        public void ChildMemberNamesFollowTheConvention()
        {
            var model = ModelLoader.Load("bellerophon.json", Fixture.Read("bellerophon.json")).Model!;
            var account = model.FindTable("Account")!;
            var symbol = model.FindTable("Symbol")!;
            var edge = model.FindTable("Edge")!;

            Assert.Equal("Account", Navigation.ChildMemberName(account, account.References.Single()));
            Assert.Equal("Symbols", Navigation.ChildMemberName(symbol, symbol.References.Single()));
            Assert.Equal("EdgesByParent", Navigation.ChildMemberName(edge, edge.References.Single(reference => reference.Name == "Parent")));
        }

        /// <summary>
        /// Two generated members with the same name are an error at the table.
        /// </summary>
        [Fact]
        public void CollidingMemberNamesAreAnError()
        {
            var text = Fixture.Read("bellerophon.json").Replace("\"key\": [\"PersonId\"],\n      \"references\": {\n        \"Node\"", "\"key\": [\"PersonId\"],\n      \"plural\": \"Accounts\",\n      \"references\": {\n        \"Node\"");

            var result = ModelLoader.Load("bellerophon.json", text);

            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.MemberCollision);
        }
    }
}
