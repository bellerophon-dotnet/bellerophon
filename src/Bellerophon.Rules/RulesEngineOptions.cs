// <copyright file="RulesEngineOptions.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Rules
{
    using System;

    /// <summary>
    /// Settings for the rules engine's cycles.
    /// </summary>
    public sealed class RulesEngineOptions
    {
        /// <summary>
        /// Gets the shortest time between the start of one tenant's cycles: the cost control.  Changes arriving in between
        /// are coalesced into the next cycle, so a longer interval means fewer, larger cycles.  The default is 50 milliseconds.
        /// </summary>
        public TimeSpan MinimumInterval { get; init; } = TimeSpan.FromMilliseconds(50);

        /// <summary>
        /// Gets how long to wait after a failed cycle before rebuilding and trying again.  The default is one second.  It also
        /// paces repeated refusals: the first refused cycle is retried at once, the second after this delay, and each further
        /// one after twice the previous pause, up to 32 times this delay.
        /// </summary>
        public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Gets the clock, so tests can control time.  The default is the system clock.
        /// </summary>
        public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    }
}
