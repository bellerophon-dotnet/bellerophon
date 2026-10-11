// <copyright file="Program.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Tool
{
    using System;
    using System.IO;
    using Bellerophon.Modeling;

    /// <summary>
    /// The Bellerophon command-line tool, installed as the <c>bell</c> command.
    /// </summary>
    public static class Program
    {
        private const string Usage = """
            Bellerophon — model-driven code generation.

            Usage:
              bell validate <path to bellerophon.json>
                  Parses and validates a model file and reports every problem found.
            """;

        /// <summary>
        /// The entry point.
        /// </summary>
        /// <param name="args">The command-line arguments.</param>
        /// <returns>0 on success, 1 if problems were found, 2 for usage errors.</returns>
        public static int Main(string[] args)
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                Console.WriteLine(Usage);
                return args.Length == 0 ? 2 : 0;
            }

            try
            {
                return args[0] switch
                {
                    "validate" => Validate(args),
                    _ => UsageError($"Unknown command '{args[0]}'."),
                };
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"error: {exception.Message}");
                return 1;
            }
        }

        private static int Validate(string[] args)
        {
            if (args.Length < 2)
            {
                return UsageError("validate requires a model file path.");
            }

            var path = args[1];
            return Report(ModelLoader.Load(path, File.ReadAllText(path)));
        }

        private static int Report(ModelLoadResult result)
        {
            foreach (var diagnostic in result.Diagnostics)
            {
                Console.WriteLine(diagnostic);
            }

            var tableCount = result.Model?.Tables.Count ?? 0;
            Console.WriteLine(result.HasErrors
                ? $"{result.Path}: invalid ({result.Diagnostics.Count} problem(s))."
                : $"{result.Path}: valid ({tableCount} table(s), {result.Diagnostics.Count} warning(s)).");
            return result.HasErrors ? 1 : 0;
        }

        private static int UsageError(string message)
        {
            Console.Error.WriteLine($"error: {message}");
            Console.Error.WriteLine(Usage);
            return 2;
        }
    }
}
