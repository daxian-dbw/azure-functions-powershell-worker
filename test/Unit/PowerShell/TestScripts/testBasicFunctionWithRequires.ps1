#
# Copyright (c) Microsoft. All rights reserved.
# Licensed under the MIT license. See LICENSE file in the project root for full license information.
#

#Requires -Modules ThreadJob

param ($req)

$module = Get-Module ThreadJob
$func = Get-Command 'TestFuncApp' -CommandType Function -ErrorAction Ignore

$result = "{0},{1},{2}" -f $req, $module.Name, $func.Name
Push-OutputBinding -Name res -Value $result
