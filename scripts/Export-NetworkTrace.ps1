# SPDX-License-Identifier: GPL-3.0-or-later
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$TracePath,

    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 2147483647)]
    [int]$ProcessId,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$trace = (Resolve-Path -LiteralPath $TracePath).Path
$output = [IO.Path]::GetFullPath($OutputPath)
$null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output))

$providers = @{
    '1c95126e-7eea-49a9-a3fe-a378b03ddb4d' = @(3006, 3008)
    '55404e71-4db9-4deb-a5f5-8f86e46dde56' = @(1000..1014)
    'bde46aea-2357-51fe-7367-d5296f530bd1' = @(1, 2, 3, 4, 5, 6, 9, 10, 11, 12, 15, 16)
}

function Convert-SocketAddress
{
    param([string]$Value)

    $hex = $Value -replace '\s', ''

    if ($hex -notmatch '^(?:[0-9a-fA-F]{2}){16,28}$')
    {
        return $Value
    }

    $bytes = [byte[]]::new($hex.Length / 2)

    for ($index = 0; $index -lt $bytes.Length; $index++)
    {
        $bytes[$index] = [Convert]::ToByte($hex.Substring($index * 2, 2), 16)
    }

    $family = [int]$bytes[0] -bor ([int]$bytes[1] -shl 8)
    $port = ([int]$bytes[2] -shl 8) -bor [int]$bytes[3]

    if ($family -eq 2 -and $bytes.Length -ge 16)
    {
        $address = [Net.IPAddress]::new([byte[]]$bytes[4..7])
        return [Net.IPEndPoint]::new($address, $port).ToString()
    }

    if ($family -eq 23 -and $bytes.Length -ge 28)
    {
        $scope = [BitConverter]::ToUInt32($bytes, 24)
        $address = [Net.IPAddress]::new([byte[]]$bytes[8..23], [long]$scope)
        return [Net.IPEndPoint]::new($address, $port).ToString()
    }

    return $Value
}

$stream = [IO.FileStream]::new($output, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
$writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
$counts = @{}
$eventCount = 0

try
{
    try
    {
        Get-WinEvent -Path $trace -Oldest -ErrorAction Stop | ForEach-Object {
            $record = $_

            try
            {
                $providerId = if ($null -eq $record.ProviderId) { '' } else { $record.ProviderId.ToString('D') }

                if ($record.ProcessId -eq $ProcessId -and $providers.ContainsKey($providerId) -and
                    $record.Id -in $providers[$providerId])
                {
                    $document = [xml]$record.ToXml()
                    $fields = [ordered]@{}

                    foreach ($field in $document.Event.EventData.Data)
                    {
                        if ($null -ne $field.Name)
                        {
                            $fields[[string]$field.Name] = [string]$field.InnerText
                        }
                    }

                    if ($fields.Contains('Address'))
                    {
                        $fields['DecodedSocketAddress'] = Convert-SocketAddress -Value $fields['Address']
                    }

                    $entry = [ordered]@{
                        TimestampUtc = $record.TimeCreated.ToUniversalTime().ToString('O')
                        ProcessId = $record.ProcessId
                        ThreadId = $record.ThreadId
                        Provider = $record.ProviderName
                        EventId = $record.Id
                        Version = $record.Version
                        Task = $record.TaskDisplayName
                        Opcode = $record.OpcodeDisplayName
                        ActivityId = $record.ActivityId
                        RelatedActivityId = $record.RelatedActivityId
                        Fields = $fields
                    }

                    $writer.WriteLine(($entry | ConvertTo-Json -Depth 6 -Compress))
                    $eventCount++
                    $key = "$($record.ProviderName)/$($record.Id)"
                    $counts[$key] = 1 + $counts[$key]
                }
            }
            finally
            {
                $record.Dispose()
            }
        }
    }
    catch
    {
        if ($_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*')
        {
            throw
        }
    }
}
finally
{
    $writer.Dispose()
}

Write-Output "Exported $eventCount process-filtered events to $output"
$counts.GetEnumerator() | Sort-Object Name | Format-Table Name,Value -AutoSize
Write-Output 'An empty export does not establish that no network work occurred. Check the capture metadata, scope, timing and lost-event counters.'
