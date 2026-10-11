// <copyright file="ModelContract.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Runtime
{
    using System;
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.Builder;
    using Microsoft.AspNetCore.Http;

    /// <summary>
    /// Refuses clients generated from a different model.  A client sends the version and fingerprint embedded in its
    /// library; if either differs from the server's, the server answers 426 Upgrade Required with its own values.
    /// </summary>
    /// <remarks>
    /// For now the rule is an exact match.  When schema evolution arrives, it relaxes to: same major version, and the
    /// client's minor version no higher than the server's.  Requests without the headers, such as tools and browsers,
    /// are not checked.
    /// </remarks>
    public sealed class ModelContractMiddleware
    {
        /// <summary>
        /// The header carrying the model version.
        /// </summary>
        public const string VersionHeader = "Bellerophon-Model-Version";

        /// <summary>
        /// The header carrying the model fingerprint.
        /// </summary>
        public const string FingerprintHeader = "Bellerophon-Model-Fingerprint";

        private readonly RequestDelegate next;

        private readonly string version;

        private readonly string fingerprint;

        /// <summary>
        /// Initializes a new instance of the <see cref="ModelContractMiddleware"/> class.
        /// </summary>
        /// <param name="next">The rest of the pipeline.</param>
        /// <param name="version">The server's model version.</param>
        /// <param name="fingerprint">The server's model fingerprint.</param>
        public ModelContractMiddleware(RequestDelegate next, string version, string fingerprint)
        {
            this.next = next;
            this.version = version;
            this.fingerprint = fingerprint;
        }

        /// <summary>
        /// Checks the request's model headers.
        /// </summary>
        /// <param name="context">The request.</param>
        /// <returns>A task that completes when the request is handled.</returns>
        public Task InvokeAsync(HttpContext context)
        {
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var headers = context.Request.Headers;
            var hasVersion = headers.TryGetValue(VersionHeader, out var clientVersion);
            var hasFingerprint = headers.TryGetValue(FingerprintHeader, out var clientFingerprint);
            if ((hasVersion || hasFingerprint) &&
                (clientVersion.ToString() != this.version || clientFingerprint.ToString() != this.fingerprint))
            {
                context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
                context.Response.Headers[VersionHeader] = this.version;
                context.Response.Headers[FingerprintHeader] = this.fingerprint;
                return Task.CompletedTask;
            }

            return this.next(context);
        }
    }

    /// <summary>
    /// Registers the model contract check.
    /// </summary>
    public static class ModelContractApplicationBuilderExtensions
    {
        /// <summary>
        /// Adds the model contract check to the request pipeline.  Generated code calls this with the model's constants.
        /// </summary>
        /// <param name="application">The application.</param>
        /// <param name="version">The server's model version.</param>
        /// <param name="fingerprint">The server's model fingerprint.</param>
        /// <returns>The same application, for chaining.</returns>
        public static IApplicationBuilder UseModelContract(this IApplicationBuilder application, string version, string fingerprint) =>
            application.UseMiddleware<ModelContractMiddleware>(version, fingerprint);
    }
}
