import XCTest

/// The presentation video's tour, filmed by presentation.yml (simctl io recordVideo): the app used as a person would,
/// on a real Jellyfin of this Mac (presentation/library.mjs). Each step printed with its time (MIRA-TOUR <step> <Unix
/// ms>), to cut the film. Skipped unless the workflow asks for it (TEST_RUNNER_MIRA_TOUR).
final class MiraTour: XCTestCase {
    @MainActor
    func testTour() throws {
        try XCTSkipUnless(ProcessInfo.processInfo.environment["MIRA_TOUR"] != nil, "Only for the presentation video")
        continueAfterFailure = true
        let app = XCUIApplication()
        app.launchArguments = ["-mira-reset", "-AppleLanguages", "(en)", "-AppleLocale", "en_US"]
        app.launch()
        let address = app.textFields["login.address"]
        XCTAssertTrue(address.waitForExistence(timeout: 15))
        focus(address); address.typeText("127.0.0.1:8096")
        app.buttons["login.continue"].tap()
        let username = app.textFields["login.username"]
        XCTAssertTrue(username.waitForExistence(timeout: 20))
        focus(username); username.typeText("Sam\n")
        let password = app.secureTextFields["login.password"]
        focus(password); password.typeText("mira-demo")
        app.buttons["login.signin"].tap()
        _ = app.staticTexts["Continue watching"].waitForExistence(timeout: 30)
        let notNow = app.buttons.matching(NSPredicate(format: "label IN %@", ["Not Now", "Pas maintenant"])).firstMatch
        if notNow.waitForExistence(timeout: 25) { notNow.tap() }
        wait(4)

        step("home")
        wait(4.5)
        scroll(app, by: 0.45); wait(1.6)
        scroll(app, by: 0.45); wait(1.6)
        scroll(app, by: 0.4); wait(1.4)
        scroll(app, by: -1.4, fast: true); wait(1.2)

        step("shows")
        tab(app, "tab.shows"); wait(2.5)
        let arcane = app.buttons.matching(NSPredicate(format: "label BEGINSWITH 'Arcane'")).firstMatch
        if arcane.waitForExistence(timeout: 10) {
            arcane.tap(); step("arcane"); wait(3.5)
            scroll(app, by: 0.5); wait(2)
            scroll(app, by: 0.45); wait(2.2)
        }

        step("movies")
        tab(app, "tab.movies"); wait(2.5)
        scroll(app, by: 0.35); wait(1.4)
        scroll(app, by: -0.35); wait(0.8)
        let verse = app.buttons.matching(NSPredicate(format: "label BEGINSWITH 'Spider-Man: Across'")).firstMatch
        for _ in 0..<5 where !(verse.exists && verse.isHittable) { scroll(app, by: 0.3); wait(0.6) }
        if verse.exists {
            verse.tap(); step("film"); wait(3.5)
            scroll(app, by: 0.45); wait(2)
            scroll(app, by: -0.6, fast: true); wait(1)
            let more = app.buttons["title.more"]
            if more.waitForExistence(timeout: 5) {
                more.tap(); wait(1.2)
                let together = app.buttons.matching(NSPredicate(format: "identifier == 'title.together' OR label == 'Watch together'")).firstMatch
                if together.waitForExistence(timeout: 5) {
                    together.tap(); step("together"); wait(3)
                    if app.buttons["together.create"].waitForExistence(timeout: 5) { app.buttons["together.create"].tap(); wait(3.5) }
                }
            }
        }

        step("search")
        tab(app, "tab.search"); wait(1.5)
        for c in "spider" { app.typeText(String(c)); Thread.sleep(forTimeInterval: 0.18) }
        wait(3.5)
        step("end")
        wait(1)
    }

    /// A tab, the bar unfolded first (a scrolled page folds it to the current tab) and a sheet closed.
    @MainActor
    private func tab(_ app: XCUIApplication, _ id: String) {
        let button = app.buttons[id]
        for _ in 0..<4 where !(button.exists && button.isHittable) {
            if app.sheets.count > 0 || app.buttons["together.create"].exists { app.swipeDown() } else { scroll(app, by: -1.4, fast: true) }
            wait(0.6)
        }
        button.tap()
    }
    @MainActor
    private func focus(_ field: XCUIElement) {
        let focused = NSPredicate(format: "hasKeyboardFocus == true")
        for _ in 0..<4 {
            if focused.evaluate(with: field) { return }
            field.tap()
            if XCTWaiter().wait(for: [XCTNSPredicateExpectation(predicate: focused, object: field)], timeout: 2) == .completed { return }
        }
    }
    private func step(_ name: String) { print("MIRA-TOUR \(name) \(Int(Date().timeIntervalSince1970 * 1000))") }
    private func wait(_ seconds: Double) { Thread.sleep(forTimeInterval: seconds) }
    /// A finger's drag: positive = toward the end of the page.
    @MainActor
    private func scroll(_ app: XCUIApplication, by fraction: Double, fast: Bool = false) {
        let from = app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: fraction > 0 ? 0.78 : 0.22))
        let to = from.withOffset(CGVector(dx: 0, dy: -fraction * app.frame.height * 0.6))
        from.press(forDuration: 0.05, thenDragTo: to, withVelocity: fast ? .fast : XCUIGestureVelocity(600), thenHoldForDuration: 0.05)
    }
}
