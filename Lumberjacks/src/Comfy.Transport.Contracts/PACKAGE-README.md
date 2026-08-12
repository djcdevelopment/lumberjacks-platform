# Comfy.Transport.Contracts

Shared routed-RPC admission and ZDO/vehicle relevance contracts for the
Lumberjacks platform and ComfyNetworkSense.

The package deliberately carries both a normal `netstandard2.0` assembly and
the same policy sources under `contentFiles`. Platform consumers reference the
assembly. The single-DLL mod consumes only `contentFiles` and compiles those
sources into its own assembly, avoiding duplicate runtime type definitions.

Published versions are immutable cross-repository boundaries. Consumers must
pin an exact version.
