import AppKit
import Darwin
import Foundation

private func writeStandardError(_ message: String) {
    FileHandle.standardError.write(Data((message + "\n").utf8))
}

private func fail(_ message: String) -> Never {
    writeStandardError("::error::\(message)")
    exit(1)
}

private final class OpenResult: @unchecked Sendable {
    private let condition = NSCondition()
    private var completed = false
    private var application: NSRunningApplication?
    private var error: Error?

    func complete(application: NSRunningApplication?, error: Error?) {
        condition.lock()
        self.application = application
        self.error = error
        completed = true
        condition.broadcast()
        condition.unlock()
    }

    func wait() -> (NSRunningApplication?, Error?) {
        condition.lock()
        while !completed {
            condition.wait()
        }
        let result = (application, error)
        condition.unlock()
        return result
    }
}

private let lifecycleObservationInterval: TimeInterval = 0.05

private func advanceMainRunLoop() {
    // The date bounds one observation interval only. It is not a launch or
    // termination deadline; the CI job owns the single 20-minute hang limit.
    RunLoop.current.run(until: Date(timeIntervalSinceNow: lifecycleObservationInterval))
}

private func waitForLaunchResolution(
    _ application: NSRunningApplication
) -> (finishedLaunching: Bool, terminated: Bool) {
    while true {
        let state = (application.isFinishedLaunching, application.isTerminated)
        if state.0 || state.1 {
            return state
        }

        advanceMainRunLoop()
    }
}

private func waitForTermination(_ application: NSRunningApplication) {
    while !application.isTerminated {
        advanceMainRunLoop()
    }
}

guard CommandLine.arguments.count == 2 else {
    fail("Usage: verify-app-bundle-launch.swift <app-bundle-path>")
}

let appURL = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
    .resolvingSymlinksInPath()
    .standardizedFileURL
guard FileManager.default.fileExists(atPath: appURL.path) else {
    fail("App bundle does not exist: \(appURL.path)")
}

let dataURL = FileManager.default.temporaryDirectory
    .appendingPathComponent("downkyi-bundle-launch-\(UUID().uuidString)", isDirectory: true)
do {
    try FileManager.default.createDirectory(at: dataURL, withIntermediateDirectories: false)
} catch {
    fail("Unable to create isolated app data directory: \(error)")
}
defer {
    try? FileManager.default.removeItem(at: dataURL)
}

let configuration = NSWorkspace.OpenConfiguration()
configuration.activates = false
configuration.addsToRecentItems = false
configuration.allowsRunningApplicationSubstitution = false
configuration.createsNewApplicationInstance = true
configuration.promptsUserIfNeeded = false
configuration.environment = ["DOWNKYI_DATA_DIR": dataURL.path]

private let openResult = OpenResult()
NSWorkspace.shared.openApplication(at: appURL, configuration: configuration) { application, error in
    openResult.complete(application: application, error: error)
}

let (openedApplication, openError) = openResult.wait()
if let openError {
    fail("macOS could not launch app bundle \(appURL.path): \(openError)")
}
guard let application = openedApplication else {
    fail("macOS reported no running application for app bundle \(appURL.path).")
}

guard let launchedBundleURL = application.bundleURL?.resolvingSymlinksInPath().standardizedFileURL,
      launchedBundleURL == appURL else {
    let actual = application.bundleURL?.path ?? "<none>"
    if !application.isTerminated {
        _ = application.forceTerminate()
        waitForTermination(application)
    }
    fail("NSWorkspace launched an unexpected bundle. expected=\(appURL.path) actual=\(actual)")
}

let launchState = waitForLaunchResolution(application)
if launchState.terminated || application.isTerminated {
    fail("App bundle terminated before macOS reported isFinishedLaunching. pid=\(application.processIdentifier)")
}
guard launchState.finishedLaunching else {
    fail("App bundle launch resolved without isFinishedLaunching. pid=\(application.processIdentifier)")
}

let pid = application.processIdentifier
print("[INFO] macOS reported isFinishedLaunching for \(appURL.path) (pid \(pid)).")

if !application.terminate() {
    if !application.isTerminated {
        _ = application.forceTerminate()
        waitForTermination(application)
    }
    fail("macOS could not complete the normal app termination request. pid=\(pid)")
}

waitForTermination(application)
print("[INFO] macOS reported isTerminated for \(appURL.path) (pid \(pid)).")
