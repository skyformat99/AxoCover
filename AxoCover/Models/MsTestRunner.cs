﻿using AxoCover.Models.Data;
using AxoCover.Models.Data.CoverageReport;
using AxoCover.Models.Data.TestReport;
using AxoCover.Models.Extensions;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AxoCover.Models
{
  public class MsTestRunner : TestRunner
  {
    private readonly Regex _outputRegex;
    private readonly string _testRunnerPath;

    private Process _testProcess;

    public MsTestRunner(IEditorContext editorContext)
    {
      _testRunnerPath = Path.Combine(editorContext.RootPath, @"mstest.exe");
      _outputRegex = new Regex(@"^(" + string.Join("|", Enum.GetNames(typeof(TestState))) + @")\s+(.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    protected override void RunTests(TestItem testItem, string testSettings)
    {
      CoverageSession coverageReport = null;
      TestRun testReport = null;
      try
      {
        var project = testItem.GetParent<TestProject>();

        if (project != null)
        {
          var testContainerPath = project.OutputFilePath;
          var testOutputPath = project.OutputDirectory;
          var testRunId = Guid.NewGuid().ToString();
          var testResultsPath = Path.Combine(testOutputPath, testRunId + ".trx");
          var coverageReportPath = Path.Combine(testOutputPath, testRunId + ".xml");
          var testFilter = testItem is TestProject ? null : testItem.FullName;
          var arguments = GetRunnerArguments(_testRunnerPath, testContainerPath, testFilter, testResultsPath, coverageReportPath, testSettings);

          var ignoredTests = testItem
            .Flatten(p => p.Children, false)
            .OfType<Data.TestMethod>()
            .Where(p => p.IsIgnored);
          foreach (var ignoredTest in ignoredTests)
          {
            OnTestExecuted(project.Name + "." + ignoredTest.FullName, TestState.Skipped);
          }

          _testProcess = new Process()
          {
            StartInfo = new ProcessStartInfo(_runnerPath, arguments)
            {
              RedirectStandardOutput = true,
              UseShellExecute = false,
              CreateNoWindow = true
            }
          };

          _testProcess.OutputDataReceived += (o, e) =>
          {
            if (e.Data == null) return;
            var text = e.Data;            

            OnTestLogAdded(text);

            var match = _outputRegex.Match(text);
            if (match.Success)
            {
              var state = (TestState)Enum.Parse(typeof(TestState), match.Groups[1].Value);
              var path = project.Name + "." + match.Groups[2].Value.Trim();

              OnTestExecuted(path, state);
            }
          };

          _testProcess.Start();
          _testProcess.BeginOutputReadLine();

          while (!_testProcess.HasExited)
          {
            _testProcess.WaitForExit(1000);
          }

          if (_isAborting) return;

          if (System.IO.File.Exists(testResultsPath))
          {
            testReport = GenericExtensions.ParseXml<TestRun>(testResultsPath);
          }

          if (System.IO.File.Exists(coverageReportPath))
          {
            coverageReport = GenericExtensions.ParseXml<CoverageSession>(coverageReportPath);
          }
        }
      }
      finally
      {
        _testProcess.Dispose();
        _testProcess = null;
        OnTestsFinished(coverageReport, testReport);
      }
    }

    private string GetRunnerArguments(string msTestPath, string testContainerPath, string testFilter, string testResultsPath, string coverageReportPath, string testSettings)
    {
      return $"-register:user -target:\"{msTestPath}\" -targetargs:\"/noisolation /testcontainer:\\\"{testContainerPath}\\\" " +
        (testFilter == null ? "" : $"/test:{testFilter} ") + (testSettings == null ? "" : $"/testsettings:\\\"{testSettings}\\\" ") +
        $"/resultsfile:\\\"{testResultsPath}\\\"\" -mergebyhash -output:\"{coverageReportPath}\"";
    }

    protected override void AbortTests()
    {
      if (_testProcess != null && !_testProcess.HasExited)
      {
        _testProcess.Kill();
      }
    }
  }
}