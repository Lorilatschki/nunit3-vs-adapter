// ***********************************************************************
// Copyright (c) 2011-2020 Charlie Poole, Terje Sandstrom
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
// ***********************************************************************

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Utilities;
using NUnit.VisualStudio.TestAdapter.NUnitEngine;
using VSTestResult = Microsoft.VisualStudio.TestPlatform.ObjectModel.TestResult;

namespace NUnit.VisualStudio.TestAdapter
{
    public interface ITestConverter
    {
        TestCase GetCachedTestCase(string id);
        TestConverter.TestResultSet GetVsTestResults(NUnitTestEventTestCase resultNode, ICollection<XmlNode> outputNodes);
    }

    public sealed class TestConverter : IDisposable, ITestConverter
    {
        private readonly ITestLogger _logger;
        private readonly Dictionary<string, TestCase> _vsTestCaseMap;
        private readonly string _sourceAssembly;
        private readonly NavigationDataProvider _navigationDataProvider;
        private bool CollectSourceInformation => adapterSettings.CollectSourceInformation;
        private readonly IAdapterSettings adapterSettings;


        public TestConverter(ITestLogger logger, string sourceAssembly, IAdapterSettings settings)
        {
            adapterSettings = settings;
            _logger = logger;
            _sourceAssembly = sourceAssembly;
            _vsTestCaseMap = new Dictionary<string, TestCase>();
            TraitsCache = new Dictionary<string, TraitsFeature.CachedTestCaseInfo>();

            if (CollectSourceInformation)
            {
                _navigationDataProvider = new NavigationDataProvider(sourceAssembly, logger);
            }
        }

        public void Dispose()
        {
            _navigationDataProvider?.Dispose();
        }

        public IDictionary<string, TraitsFeature.CachedTestCaseInfo> TraitsCache { get; }

        #region Public Methods

        /// <summary>
        /// Converts an NUnit test into a TestCase for Visual Studio,
        /// using the best method available according to the exact
        /// type passed and caching results for efficiency.
        /// </summary>
        public TestCase ConvertTestCase(NUnitTestCase testNode)
        {
            if (!testNode.IsTestCase)
                throw new ArgumentException("The argument must be a test case", nameof(testNode));

            // Return cached value if we have one
            string id = testNode.Id;
            if (_vsTestCaseMap.ContainsKey(id))
                return _vsTestCaseMap[id];

            // Convert to VS TestCase and cache the result
            var testCase = MakeTestCaseFromXmlNode(testNode);
            _vsTestCaseMap.Add(id, testCase);
            return testCase;
        }

        public TestCase GetCachedTestCase(string id)
        {
            if (_vsTestCaseMap.ContainsKey(id))
                return _vsTestCaseMap[id];

            _logger.Warning("Test " + id + " not found in cache");
            return null;
        }

        private static readonly string NL = Environment.NewLine;

        public TestResultSet GetVsTestResults(NUnitTestEventTestCase resultNode, ICollection<XmlNode> outputNodes)
        {
            var results = new List<VSTestResult>();

            var testCaseResult = GetBasicResult(resultNode, outputNodes);

            if (testCaseResult != null)
            {
                if (testCaseResult.Outcome == TestOutcome.Failed || testCaseResult.Outcome == TestOutcome.NotFound)
                {
                    testCaseResult.ErrorMessage = resultNode.Failure?.Message;
                    testCaseResult.ErrorStackTrace = resultNode.Failure?.Stacktrace;

                    // find stacktrace in assertion nodes if not defined (seems .netcore2.0 doesn't provide stack-trace for Assert.Fail("abc"))
                    if (testCaseResult.ErrorStackTrace == null)
                    {
                        string stackTrace = string.Empty;
                        foreach (XmlNode assertionStacktraceNode in resultNode.Node.SelectNodes("assertions/assertion/stack-trace"))
                        {
                            stackTrace += assertionStacktraceNode.InnerText;
                        }
                        testCaseResult.ErrorStackTrace = stackTrace;
                    }
                }
                else if (testCaseResult.Outcome == TestOutcome.Skipped || testCaseResult.Outcome == TestOutcome.None)
                {
                    testCaseResult.ErrorMessage = resultNode.ReasonMessage;
                }

                results.Add(testCaseResult);
            }

            if (results.Count == 0)
            {
                var result = MakeTestResultFromLegacyXmlNode(resultNode, outputNodes);
                if (result != null)
                    results.Add(result);
            }
            return new TestResultSet { TestCaseResult = testCaseResult, TestResults = results, ConsoleOutput = resultNode.Output };
        }

        public struct TestResultSet
        {
            public IList<VSTestResult> TestResults { get; set; }
            public TestResult TestCaseResult { get; set; }

            public string ConsoleOutput { get; set; }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Makes a TestCase from an NUnit test, adding
        /// navigation data if it can be found.
        /// </summary>
        private TestCase MakeTestCaseFromXmlNode(NUnitTestCase testNode)
        {
            string fullyQualifiedName = testNode.FullName;
            if (adapterSettings.UseParentFQNForParametrizedTests)
            {
                var parent = testNode.Parent();
                if (parent.IsParameterizedMethod)
                {
                    var parameterizedTestFullName = parent.FullName;

                    // VS expected FullyQualifiedName to be the actual class+type name,optionally with parameter types
                    // in parenthesis, but they must fit the pattern of a value returned by object.GetType().
                    // It should _not_ include custom name or param values (just their types).
                    // However, the "fullname" from NUnit's file generation is the custom name of the test, so
                    // this code must convert from one to the other.
                    // Reference: https://github.com/microsoft/vstest-docs/blob/master/RFCs/0017-Managed-TestCase-Properties.md

                    // Using the nUnit-provided "fullname" will cause failures at test execution time due to
                    // the FilterExpressionWrapper not being able to parse the test names passed-in as filters.

                    // To resolve this issue, for parameterized tests (which are the only tests that allow custom names),
                    // the parent node's "fullname" value is used instead. This is the name of the actual test method
                    // and will allow the filtering to work as expected.

                    // Note that this also means you can no longer select a single tests of these to run.
                    // When you do that, all tests within the parent node will be executed

                    if (!string.IsNullOrEmpty(parameterizedTestFullName))
                    {
                        fullyQualifiedName = parameterizedTestFullName;
                    }
                }
            }

            var testCase = new TestCase(
                                    fullyQualifiedName,
                                    new Uri(NUnitTestAdapter.ExecutorUri),
                                    _sourceAssembly)
            {
                DisplayName = testNode.Name,
                CodeFilePath = null,
                LineNumber = 0,
            };
            if (adapterSettings.UseNUnitIdforTestCaseId)
            {
                var id = testNode.Id;
                testCase.Id = EqtHash.GuidFromString(id);
            }
            if (CollectSourceInformation && _navigationDataProvider != null)
            {
                if (!CheckCodeFilePathOverride())
                {
                    var className = testNode.ClassName;
                    var methodName = testNode.MethodName;

                    var navData = _navigationDataProvider.GetNavigationData(className, methodName);
                    if (navData.IsValid)
                    {
                        testCase.CodeFilePath = navData.FilePath;
                        testCase.LineNumber = navData.LineNumber;
                    }
                }
            }
            else
            {
                _ = CheckCodeFilePathOverride();
            }

            testCase.AddTraitsFromTestNode(testNode, TraitsCache, _logger, adapterSettings);

            return testCase;

            bool CheckCodeFilePathOverride()
            {
                var codeFilePath = testNode.Properties.FirstOrDefault(p => p.Name == "_CodeFilePath");
                if (codeFilePath != null)
                {
                    testCase.CodeFilePath = codeFilePath.Value;
                    var lineNumber = testNode.Properties.FirstOrDefault(p => p.Name == "_LineNumber");
                    testCase.LineNumber = lineNumber != null ? Convert.ToInt32(lineNumber.Value) : 1;
                    return true;
                }
                return false;
            }
        }

        private VSTestResult MakeTestResultFromLegacyXmlNode(NUnitTestEventTestCase resultNode, IEnumerable<XmlNode> outputNodes)
        {
            var ourResult = GetBasicResult(resultNode, outputNodes);
            if (ourResult == null)
                return null;

            string message = resultNode.HasFailure
                ? resultNode.Failure.Message
                : resultNode.HasReason
                    ? resultNode.ReasonMessage
                    : null;

            // If we're running in the IDE, remove any caret line from the message
            // since it will be displayed using a variable font and won't make sense.
            if (!string.IsNullOrEmpty(message) && NUnitTestAdapter.IsRunningUnderIde)
            {
                string pattern = NL + "  -*\\^" + NL;
                message = Regex.Replace(message, pattern, NL, RegexOptions.Multiline);
            }

            ourResult.ErrorMessage = message;
            ourResult.ErrorStackTrace = resultNode.Failure?.Stacktrace;

            return ourResult;
        }

        private VSTestResult GetBasicResult(NUnitTestEvent resultNode, IEnumerable<XmlNode> outputNodes)
        {
            var vsTest = GetCachedTestCase(resultNode.Id);
            if (vsTest == null)
                return null;

            var vsResult = new VSTestResult(vsTest)
            {
                DisplayName = vsTest.DisplayName,
                Outcome = GetTestOutcome(resultNode),
                Duration = resultNode.Duration
            };

            var startTime = resultNode.StartTime();
            if (startTime.Ok)
                vsResult.StartTime = startTime.Time;

            var endTime = resultNode.EndTime();
            if (endTime.Ok)
                vsResult.EndTime = endTime.Time;

            // TODO: Remove this when NUnit provides a better duration
            if (vsResult.Duration == TimeSpan.Zero && (vsResult.Outcome == TestOutcome.Passed || vsResult.Outcome == TestOutcome.Failed))
                vsResult.Duration = TimeSpan.FromTicks(1);

            vsResult.ComputerName = Environment.MachineName;

            FillResultFromOutputNodes(outputNodes, vsResult);

            // Add stdOut messages from TestFinished element to vstest result
            var output = resultNode.Output;
            if (!string.IsNullOrEmpty(output))
                vsResult.Messages.Add(new TestResultMessage(TestResultMessage.StandardOutCategory, output));

            var attachmentSet = ParseAttachments(resultNode.Node);
            if (attachmentSet.Attachments.Count > 0)
                vsResult.Attachments.Add(attachmentSet);

            return vsResult;
        }

        private static void FillResultFromOutputNodes(IEnumerable<XmlNode> outputNodes, VSTestResult vsResult)
        {
            foreach (var output in outputNodes)
            {
                var stream = output.GetAttribute("stream");
                if (string.IsNullOrEmpty(stream) || IsProgressStream(stream))  // Don't add progress streams as output
                {
                    continue;
                }

                // Add stdErr/Progress messages from TestOutputXml element to vstest result
                vsResult.Messages.Add(new TestResultMessage(
                    IsErrorStream(stream)
                        ? TestResultMessage.StandardErrorCategory
                        : TestResultMessage.StandardOutCategory, output.InnerText));
            }

            bool IsErrorStream(string stream) => "error".Equals(stream, StringComparison.OrdinalIgnoreCase);

            bool IsProgressStream(string stream) => "progress".Equals(stream, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Looks for attachments in a results node and if any attachments are found they
        /// are returned"/>.
        /// </summary>
        /// <param name="resultNode">xml node for test result.</param>
        /// <returns>attachments to be added to the test, it will be empty if no attachments are found.</returns>
        private AttachmentSet ParseAttachments(XmlNode resultNode)
        {
            const string fileUriScheme = "file://";
            var attachmentSet = new AttachmentSet(new Uri(NUnitTestAdapter.ExecutorUri), "Attachments");

            foreach (XmlNode attachment in resultNode.SelectNodes("attachments/attachment"))
            {
                var path = attachment.SelectSingleNode("filePath")?.InnerText ?? string.Empty;
                var description = attachment.SelectSingleNode("description")?.InnerText;

                if (!(string.IsNullOrEmpty(path) || path.StartsWith(fileUriScheme, StringComparison.OrdinalIgnoreCase)))
                {
                    path = fileUriScheme + path;
                }

                try
                {
                    // We only support absolute paths since we dont lookup working directory here
                    // any problem with path will throw an exception
                    var fileUri = new Uri(path, UriKind.Absolute);
                    attachmentSet.Attachments.Add(new UriDataAttachment(fileUri, description));
                }
                catch (UriFormatException ex)
                {
                    _logger.Warning($"Ignoring attachment with path '{path}' due to problem with path: {ex.Message}");
                }
                catch (Exception ex)
                {
                    _logger.Warning($"Ignoring attachment with path '{path}': {ex.Message}.");
                }
            }

            return attachmentSet;
        }

        // Public for testing
        public TestOutcome GetTestOutcome(NUnitTestEvent resultNode)
        {
            return resultNode.Result() switch
            {
                NUnitTestEvent.ResultType.Success => TestOutcome.Passed,
                NUnitTestEvent.ResultType.Failed => TestOutcome.Failed,
                NUnitTestEvent.ResultType.Skipped => (resultNode.IsIgnored ? TestOutcome.Skipped : TestOutcome.None),
                NUnitTestEvent.ResultType.Warning => adapterSettings.MapWarningTo,
                _ => TestOutcome.None
            };
        }

        TestOutcome GetAssertionOutcome(XmlNode assertion)
        {
            return assertion.GetAttribute("result") switch
            {
                "Passed" => TestOutcome.Passed,
                "Failed" => TestOutcome.Failed,
                "Error" => TestOutcome.Failed,
                "Warning" => adapterSettings.MapWarningTo,
                "Inconclusive" => TestOutcome.None,
                _ => TestOutcome.None
            };
        }

        #endregion
    }
}