// <copyright file="SourceLocation.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Modeling
{
    /// <summary>
    /// A span of text in a model file.  Lines and columns are zero-based, matching Roslyn's line positions.
    /// </summary>
    /// <param name="Path">The file path.</param>
    /// <param name="Start">The zero-based character offset of the span.</param>
    /// <param name="Length">The length of the span, in characters.</param>
    /// <param name="StartLine">The zero-based line on which the span starts.</param>
    /// <param name="StartColumn">The zero-based column at which the span starts.</param>
    /// <param name="EndLine">The zero-based line on which the span ends.</param>
    /// <param name="EndColumn">The zero-based column at which the span ends.</param>
    public readonly record struct SourceLocation(
        string Path,
        int Start,
        int Length,
        int StartLine,
        int StartColumn,
        int EndLine,
        int EndColumn)
    {
        /// <summary>
        /// Gets a location that points nowhere.
        /// </summary>
        public static SourceLocation None => new SourceLocation(string.Empty, 0, 0, 0, 0, 0, 0);

        /// <summary>
        /// Formats the location the way MSBuild formats file positions: <c>path(line,column)</c>, one-based.
        /// </summary>
        /// <returns>The formatted location.</returns>
        public override string ToString() => $"{this.Path}({this.StartLine + 1},{this.StartColumn + 1})";
    }
}
