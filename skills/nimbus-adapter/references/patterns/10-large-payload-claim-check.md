# 10. Large-payload claim check

**Confidence:** production. **Applies to:** documents with many lines (quotes, orders, journals),
attachments, file feeds.

## Problem

Service Bus messages are limited: 256 KB on Standard, more on Premium. A quote with hundreds of
lines, or a base64 attachment, fails to publish, or bloats every subscriber and the audit trail.

## Solution

- **First, shrink the event.** Publish the header plus the line keys, and let the consumer read
  the lines. Or split the document into a header event and line events on the same session key.
- **If the payload must travel: claim check.**
  1. The publisher uploads the payload to blob storage in a container the integration owns.
  2. It publishes a small event carrying the blob reference (container, name, content hash and
     size).
  3. The consumer downloads it, verifies the hash, processes it, then deletes the blob (or lets
     lifecycle management expire it).
  4. Use managed identity for blob access, and never SAS URLs inside events.
- Set the threshold well below the broker limit (around 200 KB), because headers count too.

## Tests

- A payload over the threshold goes to a blob, and the event carries the reference.
- The consumer rejects a hash mismatch as a permanent failure.

## Anti-patterns

- Raising the message-size limit (Premium) instead of shaping events.
- Logging the payload while handling it.

## Provenance

- Quote payloads over 200 KB offloaded to blob storage with a reference event (production ERP
  adapter).
