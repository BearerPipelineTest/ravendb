//-----------------------------------------------------------------------
// <copyright file="PatchStatus.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

using Raven.Json.Linq;

namespace Raven.Client.Data
{
    /// <summary>
    /// The result of a patch operation
    /// </summary>
    public enum PatchStatus
    {
        /// <summary>
        /// The document does not exists, operation was a no-op
        /// </summary>
        DocumentDoesNotExist,
        /// <summary>
        /// The document did not exist, but patchIfMissing was specified and new document was created
        /// </summary>
        Created,
        /// <summary>
        /// The document was properly patched
        /// </summary>
        Patched,
        /// <summary>
        /// The document was not patched, because skipPatchIfEtagMismatch was set and the etag did not match
        /// </summary>
        Skipped,
        /// <summary>
        /// Neither document body not metadata was changed during patch operation
        /// </summary>
        NotModified
    }

    public class PatchResult
    {
        /// <summary>
        /// Result of patch operation:
        /// <para>- DocumentDoesNotExists - document does not exists, operation was a no-op,</para>
        /// <para>- Patched - document was properly patched,</para>
        /// <para>- Created - document did not exist, but patchIfMissing was specified and new document was created,</para>
        /// <para>- Skipped - document was not patched, because skipPatchIfEtagMismatch was set and the etag did not match,</para>
        /// <para>- NotModified - neither document body not metadata was changed during patch operation</para>
        /// </summary>
        public PatchStatus Status { get; set; }

        /// <summary>
        /// Patched document.
        /// </summary>
        public RavenJObject ModifiedDocument { get; set; }

        public RavenJObject OriginalDocument { get; set; }

        /// <summary>
        /// Additional debugging information (if requested).
        /// </summary>
        public RavenJObject Debug { get; set; }

        public long? Etag;

        public string Collection;
    }
}
