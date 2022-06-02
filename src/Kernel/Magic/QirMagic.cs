﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Jupyter.Core;
using Microsoft.Quantum.IQSharp.AzureClient;
using Microsoft.Quantum.IQSharp.Common;
using Microsoft.Quantum.IQSharp.Jupyter;
using Microsoft.Quantum.QsCompiler;

namespace Microsoft.Quantum.IQSharp.Kernel
{
    /// <summary>
    ///     A magic command that can be used to generate QIR from a given
    ///     operation as an entry point.
    /// </summary>
    public class QirMagic : AbstractMagic
    {
        private const string ParameterNameOperationName = "__operationName__";
        private const string ParameterNameOutputPath = "output";

        public QirMagic(ISymbolResolver resolver, IEntryPointGenerator entryPointGenerator, ILogger<SimulateMagic> logger) : base(
            "qir",
            new Microsoft.Jupyter.Core.Documentation
            {
                Summary = "Compiles a given Q# entry point to QIR, then executes the program.",
                Description = @"
                    This command takes the full name of a Q# entry point, and compiles the Q# from that entry point
                    into QIR. The resulting program is then executed, and the output of the program is displayed.

                    #### Required parameters

                    - Q# operation or function name. This must be the first parameter, and must be a valid Q# operation
                    or function name that has been defined either in the notebook or in a Q# file in the same folder.
                    - Arguments for the Q# operation or function must also be specified as `key=value` pairs.
                ".Dedent(),
                Examples = new []
                {
                    @"
                        Executes a Q# program with QIR starting at the entry point defined as `operation MyOperation() : Result`:
                        ```
                        In []: %qir MyEntryPoint
                        Out[]: <The return value of the entry point>
                        ```
                    ".Dedent(),
                    @"
                        Executes a Q# program with QIR starting at the entry point defined as `operation MyOperation(a : Int, b : Int) : Result`:
                        ```
                        In []: %trace MyOperation a=5 b=10
                        Out[]: <The return value of the entry point>
                        ```
                    ".Dedent()
                }
            }, logger)
        {
            this.EntryPointGenerator = entryPointGenerator;
            this.Logger = logger;
        }

        private ILogger? Logger { get; }

        // TODO: EntryPointGenerator might should move out of the Azure Client
        //       project.
        // GitHub Issue: https://github.com/microsoft/iqsharp/issues/610
        public IEntryPointGenerator EntryPointGenerator { get; }

        /// <inheritdoc />
        public override ExecutionResult Run(string input, IChannel channel) =>
            RunAsync(input, channel).Result;

        /// <summary>
        ///     Simulates an operation given a string with its name and a JSON
        ///     encoding of its arguments.
        /// </summary>
        public async Task<ExecutionResult> RunAsync(string input, IChannel channel)
        {
            var inputParameters = ParseInputParameters(input, firstParameterInferredName: ParameterNameOperationName);

            var name = inputParameters.DecodeParameter<string>(ParameterNameOperationName);
            var output = inputParameters.DecodeParameter<string>(ParameterNameOutputPath);

            IEntryPoint? entryPoint;
            try
            {
                entryPoint = await EntryPointGenerator.Generate(name, null, RuntimeCapability.FullComputation, generateQir: true);
            }
            catch (TaskCanceledException tce)
            {
                throw tce;
            }
            catch (CompilationErrorsException e)
            {
                var msg = $"The Q# operation {name} could not be compiled as an entry point for job execution.";
                this.Logger?.LogError(e, msg);
                channel?.Stderr(msg);
                channel?.Stderr(e.Message);
                foreach (var message in e.Errors) channel?.Stderr(message);
                return AzureClientError.InvalidEntryPoint.ToExecutionResult();
            }

            if (entryPoint == null)
            {
                return "Internal error: generated entry point was null, but no compilation errors were returned."
                    .ToExecutionResult(ExecuteStatus.Error);
            }

            if (entryPoint.QirStream == null)
            {
                return "Internal error: generated entry point does not contain a QIR bitcode stream, but no compilation errors were returned."
                    .ToExecutionResult(ExecuteStatus.Error);
            }

            using (var outStream = File.OpenWrite(output))
            {
                entryPoint.QirStream.CopyTo(outStream);
            }

            return ExecuteStatus.Ok.ToExecutionResult();
        }
    }
}
