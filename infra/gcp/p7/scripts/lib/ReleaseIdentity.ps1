<#
.SYNOPSIS
  Reads AssemblyMetadata out of a compiled .NET assembly. Dot-source it; it defines functions and
  does nothing else.

.DESCRIPTION
  Extracted from New-ReleaseCut.ps1 so the cut and the image verifier cannot drift. That is not
  tidiness: these two callers exist to answer the SAME question - "what release id does this
  artifact actually carry?" - and a release gate whose two readers disagree is worse than one
  reader, because whichever answer is convenient will be the one believed. One implementation,
  one answer.

  Windows PowerShell 5.1 - the only shell on this box - has no GAC entry for
  System.Reflection.Metadata, so `Add-Type -AssemblyName` cannot find it and PEReader below dies
  with a type-not-found. It failed *after* the source edit and both builds had already run, so the
  damage was a half-applied cut, and `-ErrorAction SilentlyContinue` hid the actual cause. The
  netstandard2.0 builds vendored in this directory do load on .NET Framework 4.8. Load those, in
  this order (Metadata needs Immutable), and never silently: a reader we cannot load must fail
  loudly, because the alternative is a cut that ships without ever checking what it shipped.

  Do not "modernize" this to `Add-Type -AssemblyName System.Reflection.Metadata`. It does not work
  on this host; the vendored DLLs are the reason this file can exist at all.

  NOTE ON $PSScriptRoot: inside a function, PowerShell resolves $PSScriptRoot to the directory of
  the file that DEFINED the function, not the one that dot-sourced it. This file lives in lib/
  alongside the vendored assemblies, so they are siblings - hence `Join-Path $PSScriptRoot $dll`,
  where the original in New-ReleaseCut.ps1 (one directory up) needed an extra 'lib' segment.
#>

function Initialize-MetadataReader {
  if ('System.Reflection.PortableExecutable.PEReader' -as [type]) { return }
  foreach ($dll in @('System.Collections.Immutable.dll', 'System.Reflection.Metadata.dll')) {
    $path = Join-Path $PSScriptRoot $dll
    if (!(Test-Path -LiteralPath $path)) { throw "metadata reader missing: $path" }
    Add-Type -Path $path
  }
  if (-not ('System.Reflection.PortableExecutable.PEReader' -as [type])) {
    throw 'metadata assemblies loaded but PEReader is still unavailable; cannot verify release identity.'
  }
}

function Get-AssemblyMetadataValue {
  param([string] $DllPath, [string] $Key)
  if (!(Test-Path -LiteralPath $DllPath)) { throw "artifact not found: $DllPath" }
  Initialize-MetadataReader
  $stream = [IO.File]::OpenRead($DllPath)
  try {
    $pe = New-Object System.Reflection.PortableExecutable.PEReader($stream)
    try {
      $md = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
      foreach ($h in $md.CustomAttributes) {
        $attr = $md.GetCustomAttribute($h)
        try {
          $blob = $md.GetBlobReader($attr.Value)
          [void]$blob.ReadUInt16()
          $k = $blob.ReadSerializedString()
          $v = $blob.ReadSerializedString()
          if ($k -eq $Key) { return $v }
        } catch { }
      }
    } finally { $pe.Dispose() }
  } finally { $stream.Dispose() }
  return $null
}
