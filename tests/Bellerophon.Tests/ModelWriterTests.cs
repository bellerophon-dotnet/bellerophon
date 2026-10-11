// <copyright file="ModelWriterTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Tests
{
    using Bellerophon.Modeling;
    using Xunit;

    /// <summary>
    /// Tests for writing models back to the file format.
    /// </summary>
    public class ModelWriterTests
    {
        /// <summary>
        /// Comments and whitespace do not change the fingerprint; a real model change does.
        /// </summary>
        [Fact]
        public void FingerprintIgnoresFormattingButNotChanges()
        {
            var text = Fixture.Read("bellerophon.json");
            var reformatted = "// A comment.\n" + text.Replace("\n", "\n\n").Replace("\"key\": [\"NodeId\"]", "\"description\": \"Documentation only.\",\n      \"key\": [\"NodeId\"]");
            var changed = text.Replace("\"maxLength\": 128", "\"maxLength\": 129");

            var fingerprint = ModelFingerprint.Compute(ModelLoader.Load("bellerophon.json", text).Model!);

            Assert.Equal(fingerprint, ModelFingerprint.Compute(ModelLoader.Load("bellerophon.json", reformatted).Model!));
            Assert.NotEqual(fingerprint, ModelFingerprint.Compute(ModelLoader.Load("bellerophon.json", changed).Model!));
            Assert.Equal(32, fingerprint.Length);
        }

        /// <summary>
        /// Writing the ledger model and loading the result produces the same semantic model.
        /// </summary>
        [Fact]
        public void WriteThenLoadRoundTrips()
        {
            var original = ModelLoader.Load("bellerophon.json", Fixture.Read("bellerophon.json"));
            Assert.False(original.HasErrors, string.Join("\n", original.Diagnostics));

            var reloaded = ModelLoader.Load("bellerophon.json", ModelWriter.Write(original.Model!));

            Assert.Empty(reloaded.Diagnostics);
            Assert.Equal(original.Model, reloaded.Model);
        }

        /// <summary>
        /// The ledger model is already in canonical form, so the writer reproduces it exactly.
        /// </summary>
        [Fact]
        public void CanonicalFileIsReproducedExactly()
        {
            var text = Fixture.Read("bellerophon.json").Replace("\r\n", "\n");

            var written = ModelWriter.Write(ModelLoader.Load("bellerophon.json", text).Model!);

            Assert.Equal(text, written);
        }
    }
}
