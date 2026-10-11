// <copyright file="EquatableArray.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Modeling
{
    using System;
    using System.Collections;
    using System.Collections.Generic;

    /// <summary>
    /// An immutable array with value equality.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <remarks>
    /// The incremental generator caches every value that passes between pipeline steps and compares it with the previous run's value.
    /// Arrays compare by reference, which would defeat the cache, so every collection in the semantic model uses this type instead.
    /// </remarks>
    public readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IReadOnlyList<T>
        where T : IEquatable<T>
    {
        /// <summary>
        /// The elements; <c>null</c> for the default (empty) value.
        /// </summary>
        private readonly T[]? items;

        /// <summary>
        /// Initializes a new instance of the <see cref="EquatableArray{T}"/> struct.
        /// </summary>
        /// <param name="items">The elements, which are copied.</param>
        public EquatableArray(IEnumerable<T> items)
        {
            this.items = new List<T>(items).ToArray();
        }

        /// <summary>
        /// Gets an empty array.
        /// </summary>
        public static EquatableArray<T> Empty => default;

        /// <inheritdoc/>
        public int Count => this.items?.Length ?? 0;

        /// <inheritdoc/>
        public T this[int index] => (this.items ?? Array.Empty<T>())[index];

        /// <summary>
        /// Compares two arrays for element-wise equality.
        /// </summary>
        /// <param name="left">The left array.</param>
        /// <param name="right">The right array.</param>
        /// <returns><c>true</c> if the arrays hold equal elements in the same order.</returns>
        public static bool operator ==(EquatableArray<T> left, EquatableArray<T> right) => left.Equals(right);

        /// <summary>
        /// Compares two arrays for element-wise inequality.
        /// </summary>
        /// <param name="left">The left array.</param>
        /// <param name="right">The right array.</param>
        /// <returns><c>true</c> if the arrays differ.</returns>
        public static bool operator !=(EquatableArray<T> left, EquatableArray<T> right) => !left.Equals(right);

        /// <inheritdoc/>
        public bool Equals(EquatableArray<T> other)
        {
            if (this.Count != other.Count)
            {
                return false;
            }

            for (var index = 0; index < this.Count; index++)
            {
                if (!EqualityComparer<T>.Default.Equals(this[index], other[index]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is EquatableArray<T> other && this.Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = 17;
                for (var index = 0; index < this.Count; index++)
                {
                    hash = (hash * 31) + EqualityComparer<T>.Default.GetHashCode(this[index]!);
                }

                return hash;
            }
        }

        /// <inheritdoc/>
        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(this.items ?? Array.Empty<T>())).GetEnumerator();

        /// <inheritdoc/>
        IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();
    }
}
