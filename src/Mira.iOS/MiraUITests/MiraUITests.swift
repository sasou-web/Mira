import XCTest

/// The app on the simulator against the stand-in Jellyfin of tools/ios/mock-jellyfin.mjs (ios.yml starts it): sign in,
/// Home, a title, playing it, Settings, sign out, in English then in French. Each screen is captured (the attachments ios.yml
/// takes out of the results), the only way to see the app without an iPhone.
final class MiraUITests: XCTestCase {
    static let server = "127.0.0.1:18097"

    override func setUp() {
        continueAfterFailure = false
    }

    @MainActor
    func testEnglish() throws {
        let app = launch(language: "en", locale: "en_US")
        capture("01-login")
        signIn(app, password: "mira")
        XCTAssertTrue(app.staticTexts["Continue watching"].waitForExistence(timeout: 20), "Home never showed Continue watching")
        dismissSavePassword(app)
        settle()
        capture("02-home")
        app.swipeUp()
        settle()
        capture("03-home-rows")

        let poster = app.buttons["Tears of Steel, 2012"].firstMatch
        XCTAssertTrue(poster.waitForExistence(timeout: 10), "No card for Tears of Steel")
        poster.tap()
        XCTAssertTrue(app.staticTexts["title.name"].waitForExistence(timeout: 10), "The title page did not open")
        settle()
        capture("04-title")
        XCTAssertTrue(app.buttons["title.play"].exists, "No Play button on the title page")

        // Play: Apple's player over the app, the file as it is; Jellyfin hears where it is, then where it stopped.
        app.buttons["title.play"].tap()
        XCTAssertTrue(waitForReport("/Start", timeout: 30), "Jellyfin never heard that playback started")
        Thread.sleep(forTimeInterval: 1.5)
        capture("05-player")
        app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5)).tap()
        let close = app.buttons.matching(NSPredicate(format: "label IN %@", ["Close", "Done", "Dismiss"])).firstMatch
        if close.waitForExistence(timeout: 3) { close.tap() } else { app.swipeDown() }
        XCTAssertTrue(waitForReport("/Stopped", timeout: 20), "Jellyfin never heard that playback stopped")
        XCTAssertTrue(app.buttons["title.play"].waitForExistence(timeout: 10), "Closing the player did not lead back to the title page")
        app.navigationBars.buttons.firstMatch.tap()

        // A series: Play for its next episode, then its episodes, each with a Play of its own.
        let series = app.buttons["Pioneer One, 2010"].firstMatch
        XCTAssertTrue(series.waitForExistence(timeout: 10), "No card for Pioneer One")
        series.tap()
        // The episodes come after the summary, drawn as they scroll into view.
        let third = app.buttons["episode.play.3"]
        for _ in 0..<4 where !third.waitForExistence(timeout: 4) { app.swipeUp() }
        settle()
        capture("06-series")
        XCTAssertTrue(third.exists, "The series page lists no episodes")
        app.navigationBars.buttons.firstMatch.tap()

        // Scrolling down Home folded the tab bar into its Home button; back at the top, it shows every tab again.
        app.swipeDown()
        app.swipeDown()
        let movies = app.tabBars.buttons["Movies"]
        XCTAssertTrue(movies.waitForExistence(timeout: 5), "The tab bar did not unfold back at the top of Home")
        movies.tap()

        // Movies: every film in a grid, then only the favorites.
        XCTAssertTrue(app.staticTexts["8 movies"].waitForExistence(timeout: 15), "Movies does not count the 8 films")
        XCTAssertTrue(app.buttons["Sprite Fright, 2021"].waitForExistence(timeout: 5), "Movies shows no grid of films")
        settle()
        capture("07-movies")
        app.buttons["library.favorites"].tap()
        XCTAssertTrue(app.staticTexts["3 movies"].waitForExistence(timeout: 10), "Favorites does not keep only the 3 favorite films")

        // Search: an episode found by its own name.
        app.tabBars.buttons["Search"].tap()
        let field = app.searchFields.firstMatch
        XCTAssertTrue(field.waitForExistence(timeout: 10), "Search has no search field")
        field.tap()
        field.typeText("man")
        XCTAssertTrue(app.buttons.matching(NSPredicate(format: "label CONTAINS 'The Man from Mars'")).firstMatch.waitForExistence(timeout: 15),
                      "Search did not find The Man from Mars")
        settle()
        capture("08-search")
        // Out of the search field: the tab bar comes back in place of the keyboard.
        field.typeText("\n")
        let settings = app.tabBars.buttons["Settings"]
        if !settings.waitForExistence(timeout: 3) || !settings.isHittable {
            let leave = app.buttons.matching(NSPredicate(format: "label IN %@", ["Cancel", "Close"])).firstMatch
            if leave.waitForExistence(timeout: 3) { leave.tap() }
        }
        XCTAssertTrue(settings.waitForExistence(timeout: 5), "No Settings tab")
        settings.tap()
        XCTAssertTrue(app.buttons["settings.signout"].waitForExistence(timeout: 10))
        capture("09-settings")
        app.buttons["settings.signout"].tap()
        // The dialog's button, by its identifier or, if iOS does not pass it on, by its name.
        let confirm = app.buttons.matching(NSPredicate(format: "identifier == 'settings.signout.confirm' OR (label == 'Sign out' AND identifier != 'settings.signout')")).firstMatch
        XCTAssertTrue(confirm.waitForExistence(timeout: 5), "No confirmation to sign out")
        confirm.tap()
        XCTAssertTrue(app.textFields["login.address"].waitForExistence(timeout: 10), "Signing out did not lead back to the sign-in screen")
    }

    @MainActor
    func testFrench() throws {
        let app = launch(language: "fr", locale: "fr_FR")
        capture("11-connexion")
        signIn(app, password: "faux")
        XCTAssertTrue(app.staticTexts["Nom d’utilisateur ou mot de passe incorrect."].waitForExistence(timeout: 10), "No message for a wrong password")
        capture("12-mot-de-passe-faux")
        let password = app.secureTextFields["login.password"]
        focus(password)
        password.typeText("mira\n")
        XCTAssertTrue(app.staticTexts["Continuer à regarder"].waitForExistence(timeout: 20), "L'Accueil n'a pas montré Continuer à regarder")
        dismissSavePassword(app)
        settle()
        capture("13-accueil")
        app.tabBars.buttons["Réglages"].tap()
        XCTAssertTrue(app.buttons["settings.signout"].waitForExistence(timeout: 10))
        capture("14-reglages")
    }

    @MainActor
    private func launch(language: String, locale: String) -> XCUIApplication {
        let app = XCUIApplication()
        app.launchArguments = ["-mira-reset", "-AppleLanguages", "(\(language))", "-AppleLocale", locale]
        app.launch()
        return app
    }

    @MainActor
    private func signIn(_ app: XCUIApplication, password: String) {
        let address = app.textFields["login.address"]
        XCTAssertTrue(address.waitForExistence(timeout: 10))
        focus(address)
        address.typeText(Self.server)
        app.buttons["login.continue"].tap()
        let username = app.textFields["login.username"]
        XCTAssertTrue(username.waitForExistence(timeout: 15), "The server was not found")
        focus(username)
        // Return goes on to the password, as a person would.
        username.typeText("ogsas\n")
        let field = app.secureTextFields["login.password"]
        focus(field)
        field.typeText(password)
        app.buttons["login.signin"].tap()
    }

    /// Gives a field the keyboard: a tap can land while the form still scrolls clear of the keyboard and miss.
    @MainActor
    private func focus(_ field: XCUIElement) {
        let focused = NSPredicate(format: "hasKeyboardFocus == true")
        for _ in 0..<3 {
            if focused.evaluate(with: field) { return }
            field.tap()
            if XCTWaiter().wait(for: [XCTNSPredicateExpectation(predicate: focused, object: field)], timeout: 2) == .completed { return }
        }
        XCTFail("\(field.identifier) never got the keyboard")
    }

    /// iOS offers to save the password after a sign-in, over Home, from 2 to more than 6 seconds later on a busy
    /// runner: waited for long enough that no later tap lands on it.
    @MainActor
    private func dismissSavePassword(_ app: XCUIApplication) {
        let button = app.buttons.matching(NSPredicate(format: "label IN %@", ["Not Now", "Pas maintenant"])).firstMatch
        if button.waitForExistence(timeout: 20) { button.tap() }
    }

    /// Waits for the stand-in Jellyfin to have received this report of the player (/Start, /Progress, /Stopped).
    private func waitForReport(_ kind: String, timeout: TimeInterval) -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        let url = URL(string: "http://\(Self.server)/Mock/Reports")!
        while Date() < deadline {
            if let data = try? Data(contentsOf: url), let reports = try? JSONSerialization.jsonObject(with: data) as? [[String: Any]],
               reports.contains(where: { $0["kind"] as? String == kind }) {
                return true
            }
            Thread.sleep(forTimeInterval: 0.5)
        }
        return false
    }

    /// Pictures fade in from the server: a moment before a capture.
    private func settle() { Thread.sleep(forTimeInterval: 2.5) }

    @MainActor
    private func capture(_ name: String) {
        let attachment = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
        attachment.name = name
        attachment.lifetime = .keepAlways
        add(attachment)
    }
}
