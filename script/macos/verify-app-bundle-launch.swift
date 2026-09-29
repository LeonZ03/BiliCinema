import AppKit
import CoreFoundation
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

private final class ApplicationLifecycleState: @unchecked Sendable {
    private let lock = NSLock()
    private var finishedLaunching = false
    private var terminated = false

    func update(from application: NSRunningApplication) {
        lock.lock()
        finishedLaunching = application.isFinishedLaunching
        terminated = application.isTerminated
        lock.unlock()

        let mainRunLoop = CFRunLoopGetMain()
        CFRunLoopPerformBlock(mainRunLoop, CFRunLoopMode.defaultMode.rawValue) {
            CFRunLoopStop(mainRunLoop)
        }
        CFRunLoopWakeUp(mainRunLoop)
    }

    func waitForLaunchResolution() -> (finishedLaunching: Bool, terminated: Bool) {
        while true {
            lock.lock()
            let result = (finishedLaunching, terminated)
            lock.unlock()
            if result.0 || result.1 {
                return result
            }

            CFRunLoopRun()
        }
    }

    func waitForTermination() {
        while true {
            lock.lock()
            let isTerminated = terminated
            lock.unlock()
            if isTerminated {
                return
            }

            CFRunLoopRun()
        }
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

private let lifecycle = ApplicationLifecycleState()
let launchObservation = application.observe(\.isFinishedLaunching, options: [.initial, .new]) {
    runningApplication, _ in
    lifecycle.update(from: runningApplication)
}
let terminationObservation = application.observe(\.isTerminated, options: [.initial, .new]) {
    runningApplication, _ in
    lifecycle.update(from: runningApplication)
}
defer {
    launchObservation.invalidate()
    terminationObservation.invalidate()
}

guard let launchedBundleURL = application.bundleURL?.resolvingSymlinksInPath().standardizedFileURL,
      launchedBundleURL == appURL else {
    let actual = application.bundleURL?.path ?? "<none>"
    if !application.isTerminated {
        _ = application.forceTerminate()
        lifecycle.waitForTermination()
    }
    fail("NSWorkspace launched an unexpected bundle. expected=\(appURL.path) actual=\(actual)")
}

let launchState = lifecycle.waitForLaunchResolution()
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
        lifecycle.waitForTermination()
    }
    fail("macOS could not complete the normal app termination request. pid=\(pid)")
}

lifecycle.waitForTermination()
print("[INFO] macOS reported isTerminated for \(appURL.path) (pid \(pid)).")
