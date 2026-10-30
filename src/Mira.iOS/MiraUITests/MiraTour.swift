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

    /// The presentation video's pictures, for a montage that scrolls and zooms them at 60 frames a second (the
    /// simulator films at 15): each page from its top, then after each slow drag held still (no momentum), so that the
    /// montage can join them; the search typed letter by letter; Mira's player sideways on Spring. Attachments named
    /// shot-<page>-<n>, read from the result bundle by presentation.yml. Skipped unless it asks (TEST_RUNNER_MIRA_SHOTS).
    @MainActor
    func testShots() throws {
        try XCTSkipUnless(ProcessInfo.processInfo.environment["MIRA_SHOTS"] != nil, "Only for the presentation video")
        continueAfterFailure = true
        let app = XCUIApplication()
        app.launchArguments = ["-mira-reset", "-mira-controls-stay", "-AppleLanguages", "(en)", "-AppleLocale", "en_US"]
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
        wait(6)

        shot("home-00"); drags(app, "home", 9)
        tab(app, "tab.shows"); wait(3); shot("shows-00"); drags(app, "shows", 2)
        scroll(app, by: -1.4, fast: true); wait(1)
        let arcane = app.buttons.matching(NSPredicate(format: "label BEGINSWITH 'Arcane'")).firstMatch
        if arcane.waitForExistence(timeout: 10) { arcane.tap(); wait(4); shot("arcane-00"); drags(app, "arcane", 8) }

        tab(app, "tab.movies"); wait(3); shot("movies-00"); drags(app, "movies", 6)
        scroll(app, by: -1.4, fast: true); scroll(app, by: -1.4, fast: true); wait(1)
        let verse = app.buttons.matching(NSPredicate(format: "label BEGINSWITH 'Spider-Man: Across'")).firstMatch
        for _ in 0..<6 where !(verse.exists && verse.isHittable) { scroll(app, by: 0.3); wait(0.8) }
        if verse.exists { verse.tap(); wait(4); shot("verse-00"); drags(app, "verse", 4) }

        tab(app, "tab.movies"); wait(2)
        let spring = app.buttons.matching(NSPredicate(format: "label BEGINSWITH 'Spring'")).firstMatch
        for _ in 0..<8 where !(spring.exists && spring.isHittable) { scroll(app, by: 0.3); wait(0.8) }
        if spring.exists {
            spring.tap(); wait(4); shot("spring-00"); drags(app, "spring", 2)
            scroll(app, by: -1.4, fast: true); wait(1)
            let start = app.buttons["title.play"]
            if start.waitForExistence(timeout: 10) {
                start.tap()
                XCUIDevice.shared.orientation = .landscapeLeft
                wait(8)
                let play = app.buttons["player.play"]
                for _ in 0..<3 where !play.waitForExistence(timeout: 2) { app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5)).tap() }
                if play.exists && play.label == "Play" { play.tap() }
                wait(1.5)
                for i in 0..<6 { shot(String(format: "player-%02d", i)); wait(0.7) }
                app.coordinate(withNormalizedOffset: CGVector(dx: 0.86, dy: 0.5)).doubleTap()
                shot("player-skip"); wait(2)
                let showcase = app.buttons["player.showcase"]
                if showcase.exists { showcase.tap(); wait(2.5); shot("player-scrub"); for _ in 0..<4 { showcase.tap() }; wait(1) }
                let minimize = app.buttons["player.minimize"]
                if minimize.waitForExistence(timeout: 5) { minimize.tap() }
                XCUIDevice.shared.orientation = .portrait
                wait(3); shot("mini-00")
                let close = app.buttons["mini.close"]
                if close.waitForExistence(timeout: 5) { close.tap() }
                wait(2)
            }
        }

        tab(app, "tab.search"); wait(2); shot("search-00")
        for (i, c) in "spider".enumerated() { app.typeText(String(c)); shot(String(format: "search-%02d", i + 1)) }
        wait(4); shot("search-final")
        app.swipeUp(); wait(2); shot("search-final2")
    }
    /// Slow drags toward the end of the page, held still before the finger lifts, a picture after each.
    @MainActor
    private func drags(_ app: XCUIApplication, _ page: String, _ count: Int) {
        for i in 1...count {
            let from = app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.7))
            let to = from.withOffset(CGVector(dx: 0, dy: -0.28 * app.frame.height))
            from.press(forDuration: 0.08, thenDragTo: to, withVelocity: XCUIGestureVelocity(220), thenHoldForDuration: 0.7)
            wait(1.6)
            shot(String(format: "%@-%02d", page, i))
        }
    }
    @MainActor
    private func shot(_ name: String) {
        let attachment = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
        attachment.name = "shot-" + name
        attachment.lifetime = .keepAlways
        add(attachment)
        step("shot-" + name)
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
