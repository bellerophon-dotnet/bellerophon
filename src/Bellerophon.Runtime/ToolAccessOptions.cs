// <copyright file="ToolAccessOptions.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Runtime
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The applications allowed to act as tools: callers that act as themselves, with application permissions, and so skip
    /// the tenant membership check (the operator tool, bootstrap scripts).  Every other caller must be a user of the tenant.
    /// </summary>
    /// <remarks>
    /// A caller is a tool only if its token says it is an application's (<c>idtyp</c> is <c>app</c>, which needs the
    /// <c>idtyp</c> optional claim on the API's app registration), carries no delegated scope, and names one of these client
    /// IDs.  An empty list means no tools: fail closed.  Entries that are not GUIDs, such as the placeholders shipped in
    /// configuration, never match.
    /// </remarks>
    public sealed class ToolAccessOptions
    {
        /// <summary>
        /// The configuration section these options are read from.
        /// </summary>
        public const string SectionName = "Tools";

        /// <summary>
        /// Gets or sets the client IDs (application IDs) of the app registrations allowed to act as tools.
        /// </summary>
        public IReadOnlyList<string> ClientIds { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Gets a value indicating whether a configured entry is a placeholder rather than a client ID.
        /// </summary>
        /// <param name="clientId">The configured entry.</param>
        /// <returns><c>true</c> if it is not a GUID, and so matches no application.</returns>
        public static bool IsPlaceholder(string clientId) => !Guid.TryParse(clientId, out _);

        /// <summary>
        /// Gets a value indicating whether an application is allowed to act as a tool.
        /// </summary>
        /// <param name="clientId">The application's client ID, from its token.</param>
        /// <returns><c>true</c> if it is on the list.</returns>
        public bool Allows(string clientId)
        {
            if (IsPlaceholder(clientId))
            {
                return false;
            }

            foreach (var allowed in this.ClientIds)
            {
                if (!IsPlaceholder(allowed) && string.Equals(allowed, clientId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
