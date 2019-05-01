//
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;

using Microsoft.Azure.Functions.PowerShellWorker.Utility;
using Microsoft.Azure.WebJobs.Script.Grpc.Messages;
using System.Management.Automation.Runspaces;
using LogLevel = Microsoft.Azure.WebJobs.Script.Grpc.Messages.RpcLog.Types.Level;

namespace Microsoft.Azure.Functions.PowerShellWorker.PowerShell
{
    using System.Management.Automation;

    internal class PowerShellManager
    {
        private const BindingFlags NonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        private readonly static object[] _argumentsGetJobs = new object[] { null, false, false, null };
        private readonly static MethodInfo _methodGetJobs = typeof(JobManager).GetMethod(
            "GetJobs",
            NonPublicInstance,
            binder: null,
            callConvention: CallingConventions.Any,
            new Type[] { typeof(Cmdlet), typeof(bool), typeof(bool), typeof(string[]) },
            modifiers: null);

        private readonly ILogger _logger;
        private readonly PowerShell _pwsh;
        private bool _runspaceInited;

        /// <summary>
        /// Gets the Runspace InstanceId.
        /// </summary>
        internal Guid InstanceId => _pwsh.Runspace.InstanceId;

        /// <summary>
        /// Gets the associated logger.
        /// </summary>
        internal ILogger Logger => _logger;

        static PowerShellManager()
        {
            // Set the type accelerators for 'HttpResponseContext' and 'HttpResponseContext'.
            // We probably will expose more public types from the worker in future for the interop between worker and the 'PowerShellWorker' module.
            // But it's most likely only 'HttpResponseContext' and 'HttpResponseContext' are supposed to be used directly by users, so we only add
            // type accelerators for these two explicitly.
            var accelerator = typeof(PSObject).Assembly.GetType("System.Management.Automation.TypeAccelerators");
            var addMethod = accelerator.GetMethod("Add", new Type[] { typeof(string), typeof(Type) });
            addMethod.Invoke(null, new object[] { "HttpResponseContext", typeof(HttpResponseContext) });
            addMethod.Invoke(null, new object[] { "HttpRequestContext", typeof(HttpRequestContext) });
        }

        internal PowerShellManager(ILogger logger, bool delayInit = false)
        {
            if (FunctionLoader.FunctionAppRootPath == null)
            {
                throw new InvalidOperationException(PowerShellWorkerStrings.FunctionAppRootNotResolved);
            }

            _logger = logger;
            _pwsh = PowerShell.Create(Utils.SingletonISS.Value);

            // Setup Stream event listeners
            var streamHandler = new StreamHandler(logger);
            _pwsh.Streams.Debug.DataAdding += streamHandler.DebugDataAdding;
            _pwsh.Streams.Error.DataAdding += streamHandler.ErrorDataAdding;
            _pwsh.Streams.Information.DataAdding += streamHandler.InformationDataAdding;
            _pwsh.Streams.Progress.DataAdding += streamHandler.ProgressDataAdding;
            _pwsh.Streams.Verbose.DataAdding += streamHandler.VerboseDataAdding;
            _pwsh.Streams.Warning.DataAdding += streamHandler.WarningDataAdding;

            // Initialize the Runspace
            if (!delayInit)
            {
                Initialize();
            }
        }

        /// <summary>
        /// Extra initialization of the Runspace.
        /// </summary>
        internal void Initialize()
        {
            if (!_runspaceInited)
            {
                // Invoke the profile
                InvokeProfile(FunctionLoader.FunctionAppProfilePath);

                // Deploy functions from the function App
                DeployAzFunctionToRunspace();
                _runspaceInited = true;
            }
        }

        /// <summary>
        /// Create the PowerShell function that is equivalent to the 'scriptFile' when possible.
        /// </summary>
        private void DeployAzFunctionToRunspace()
        {
            foreach (AzFunctionInfo functionInfo in FunctionLoader.GetLoadedFunctions())
            {
                if (functionInfo.FuncScriptBlock != null)
                {
                    _pwsh.Runspace.SessionStateProxy.InvokeProvider.Item.New(
                        @"Function:\",
                        functionInfo.FuncName,
                        itemTypeName: null,
                        functionInfo.FuncScriptBlock);
                }
            }
        }

        /// <summary>
        /// This method invokes the FunctionApp's profile.ps1.
        /// </summary>
        internal void InvokeProfile(string profilePath)
        {
            Exception exception = null;
            if (profilePath == null)
            {
                string noProfileMsg = string.Format(PowerShellWorkerStrings.FileNotFound, "profile.ps1", FunctionLoader.FunctionAppRootPath);
                _logger.Log(LogLevel.Trace, noProfileMsg);
                return;
            }

            try
            {
                // Import-Module on a .ps1 file will evaluate the script in the global scope.
                _pwsh.AddCommand(Utils.ImportModuleCmdletInfo)
                        .AddParameter("Name", profilePath)
                        .AddParameter("PassThru", Utils.BoxedTrue)
                     .AddCommand(Utils.RemoveModuleCmdletInfo)
                        .AddParameter("Force", Utils.BoxedTrue)
                        .AddParameter("ErrorAction", "SilentlyContinue")
                     .InvokeAndClearCommands();
            }
            catch (Exception e)
            {
                exception = e;
                throw;
            }
            finally
            {
                if (_pwsh.HadErrors)
                {
                    string errorMsg = string.Format(PowerShellWorkerStrings.FailToRunProfile, profilePath);
                    _logger.Log(LogLevel.Error, errorMsg, exception, isUserLog: true);
                }
            }
        }

        /// <summary>
        /// Execution a function fired by a trigger or an activity function scheduled by an orchestration.
        /// </summary>
        internal Hashtable InvokeFunction(
            AzFunctionInfo functionInfo,
            Hashtable triggerMetadata,
            IList<ParameterBinding> inputData)
        {
            string scriptPath = functionInfo.ScriptPath;
            string entryPoint = functionInfo.EntryPoint;

            try
            {
                if (string.IsNullOrEmpty(entryPoint))
                {
                    _pwsh.AddCommand(functionInfo.FuncScriptBlock != null ? functionInfo.FuncName : scriptPath);
                }
                else
                {
                    // If an entry point is defined, we import the script module.
                    _pwsh.AddCommand(Utils.ImportModuleCmdletInfo)
                            .AddParameter("Name", scriptPath)
                         .InvokeAndClearCommands();

                    _pwsh.AddCommand(entryPoint);
                }

                // Set arguments for each input binding parameter
                foreach (ParameterBinding binding in inputData)
                {
                    string bindingName = binding.Name;
                    if (functionInfo.FuncParameters.TryGetValue(bindingName, out PSScriptParamInfo paramInfo))
                    {
                        var bindingInfo = functionInfo.InputBindings[bindingName];
                        var valueToUse = Utils.TransformInBindingValueAsNeeded(paramInfo, bindingInfo, binding.Data.ToObject());
                        _pwsh.AddParameter(bindingName, valueToUse);
                    }
                }

                // Gives access to additional Trigger Metadata if the user specifies TriggerMetadata
                if(functionInfo.HasTriggerMetadataParam)
                {
                    _pwsh.AddParameter(AzFunctionInfo.TriggerMetadata, triggerMetadata);
                }

                Collection<object> pipelineItems = _pwsh.AddCommand("Microsoft.Azure.Functions.PowerShellWorker\\Trace-PipelineObject")
                                                        .InvokeAndClearCommands<object>();

                Hashtable outputBindings = FunctionMetadata.GetOutputBindingHashtable(_pwsh.Runspace.InstanceId);
                Hashtable result = new Hashtable(outputBindings, StringComparer.OrdinalIgnoreCase);
                outputBindings.Clear();

                /*
                 * TODO: See GitHub issue #82. We are not settled on how to handle the Azure Functions concept of the $returns Output Binding
                if (pipelineItems != null && pipelineItems.Count > 0)
                {
                    // If we would like to support Option 1 from #82, use the following 3 lines of code:                    
                    object[] items = new object[pipelineItems.Count];
                    pipelineItems.CopyTo(items, 0);
                    result.Add(AzFunctionInfo.DollarReturn, items);

                    // If we would like to support Option 2 from #82, use this line:
                    result.Add(AzFunctionInfo.DollarReturn, pipelineItems[pipelineItems.Count - 1]);
                }
                */

                return result;
            }
            finally
            {
                ResetRunspace();
            }
        }

        private void ResetRunspace()
        {
            var jobs = (List<Job2>)_methodGetJobs.Invoke(_pwsh.Runspace.JobManager, _argumentsGetJobs);
            if (jobs != null && jobs.Count > 0)
            {
                // Clean up jobs started during the function execution.
                _pwsh.AddCommand(Utils.RemoveJobCmdletInfo)
                        .AddParameter("Force", Utils.BoxedTrue)
                        .AddParameter("ErrorAction", "SilentlyContinue")
                     .InvokeAndClearCommands(jobs);
            }
        }
    }
}
