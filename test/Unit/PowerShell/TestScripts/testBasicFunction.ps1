#
# Copyright (c) Microsoft. All rights reserved.
# Licensed under the MIT license. See LICENSE file in the project root for full license information.
#

param ($Req)

# Used for logging tests
Write-Verbose "a log"
$func = Get-Command 'TestFuncApp' -CommandType Function

$result = "{0},{1}" -f $Req, $func.Name
Push-OutputBinding -Name res -Value $result
