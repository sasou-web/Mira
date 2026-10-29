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
        // Favorite, then not: Jellyfin is told each time, and the page reads it back.
        let favorite = app.buttons["title.favorite"]
        XCTAssertTrue(favorite.waitForExistence(timeout: 5), "The title page has no Favorite")
        favorite.tap()
        XCTAssertTrue(app.buttons.matching(NSPredicate(format: "identifier == 'title.favorite' AND label == 'In favorites'")).firstMatch.waitForExistence(timeout: 10),
                      "Favorite did not stick")
        favorite.tap()
        XCTAssertTrue(app.buttons.matching(NSPredicate(format: "identifier == 'title.favorite' AND label == 'Favorite'")).firstMatch.waitForExistence(timeout: 10),
                      "Taking the title out of favorites did not stick")

        // Watch together: a group made here, the title put on for it from Play; Jellyfin's socket tells the player
        // when to start, and it says it is ready; then out of the group.
        let create = app.buttons["together.create"]
        for _ in 0..<4 where !create.isHittable { app.swipeUp() }
        XCTAssertTrue(create.waitForExistence(timeout: 5), "The title page has no Create a group")
        create.tap()
        let group = app.descendants(matching: .any)["together.group"]
        XCTAssertTrue(group.waitForExistence(timeout: 15), "Making a group did not show it on the title page")
        settle()
        capture("05b-together")
        let play = app.buttons["title.play"]
        for _ in 0..<4 where !play.isHittable { app.swipeDown() }
        XCTAssertEqual(play.label, "Start for the group")
        play.tap()
        XCTAssertTrue(waitForSync("SetNewQueue", timeout: 30), "Play did not put the title on for the group")
        XCTAssertTrue(waitForSync("Ready", timeout: 30), "The player never told the group it was ready")
        Thread.sleep(forTimeInterval: 2.5)
        capture("05c-together-player")
        app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5)).tap()
        if close.waitForExistence(timeout: 3) { close.tap() } else { app.swipeDown() }
        XCTAssertTrue(waitForSync("SetIgnoreWait", timeout: 20), "Closing the player did not tell the group to go on without it")
        let leave = app.buttons["together.leave"]
        for _ in 0..<4 where !leave.isHittable { app.swipeUp() }
        XCTAssertTrue(leave.waitForExistence(timeout: 10), "No Leave the group")
        leave.tap()
        XCTAssertTrue(create.waitForExistence(timeout: 15), "Leaving the group did not bring back Create a group")
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
        if !settings.waitForExistence(timeout: 3) {
            // iOS 26 keeps the search field in place of the tabs, with a close button on its right.
            let box = field.frame
            app.coordinate(withNormalizedOffset: .zero)
                .withOffset(CGVector(dx: min(box.maxX + 30, app.frame.maxX - 20), dy: box.midY)).tap()
        }
        // Then the tab bar is the search field and the last tab before Search: back to it, the tabs unfold.
        if !settings.waitForExistence(timeout: 3) {
            let back = app.buttons["Movies"].firstMatch
            if back.waitForExistence(timeout: 3) { back.tap() }
        }
        if !settings.waitForExistence(timeout: 5) {
            capture("08-search-left")
            XCTFail("No Settings tab; buttons: \(app.buttons.allElementsBoundByIndex.map { "\($0.label)|\($0.identifier)|\($0.frame)" })")
        }
        settings.tap()
        XCTAssertTrue(app.navigationBars["Settings"].waitForExistence(timeout: 10), "Settings did not open")
        settle()
        capture("09-settings")
        let signOut = signOutButton(app)
        XCTAssertTrue(signOut.exists, "No Sign out at the bottom of Settings")
        capture("09b-settings-bottom")
        signOut.tap()
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
        let poster = app.buttons["Tears of Steel, 2012"].firstMatch
        XCTAssertTrue(poster.waitForExistence(timeout: 10), "Pas de carte pour Tears of Steel")
        poster.tap()
        XCTAssertTrue(app.staticTexts["title.name"].waitForExistence(timeout: 10), "La fiche ne s'est pas ouverte")
        settle()
        capture("13b-fiche")
        app.swipeUp()
        settle()
        capture("13c-fiche-suite")
        app.navigationBars.buttons.firstMatch.tap()
        app.swipeDown()
        app.tabBars.buttons["Réglages"].tap()
        XCTAssertTrue(app.navigationBars["Réglages"].waitForExistence(timeout: 10), "Les Réglages ne se sont pas ouverts")
        settle()
        capture("14-reglages")
        XCTAssertTrue(signOutButton(app).exists, "Pas de Se déconnecter en bas des Réglages")
        capture("14b-reglages-bas")
    }

    /// The largest text a person may choose short of the accessibility sizes' last ones, in French (the longer
    /// language): every screen still reads, nothing cut.
    @MainActor
    func testLargeText() throws {
        let app = launch(language: "fr", locale: "fr_FR", extra: ["-UIPreferredContentSizeCategoryName", "UICTContentSizeCategoryAccessibilityL"])
        signIn(app, password: "mira")
        XCTAssertTrue(app.staticTexts["Continuer à regarder"].waitForExistence(timeout: 20), "L'Accueil n'a pas montré Continuer à regarder")
        dismissSavePassword(app)
        settle()
        capture("21-accueil-grand")
        // The tabs first: once a page has scrolled, the tab bar keeps only the current tab.
        let settings = app.tabBars.buttons["Réglages"]
        XCTAssertTrue(settings.waitForExistence(timeout: 5), "Pas d'onglet Réglages en grand texte")
        settings.tap()
        settle()
        capture("24-reglages-grand")
        let films = app.tabBars.buttons["Films"]
        XCTAssertTrue(films.waitForExistence(timeout: 5), "Pas d'onglet Films en grand texte")
        films.tap()
        settle()
        capture("23-films-grand")
        let poster = app.buttons["Tears of Steel, 2012"].firstMatch
        XCTAssertTrue(poster.waitForExistence(timeout: 10), "Tears of Steel absent des films")
        poster.tap()
        XCTAssertTrue(app.staticTexts["title.name"].waitForExistence(timeout: 10), "La fiche ne s'est pas ouverte")
        settle()
        capture("22-fiche-grand")
        app.swipeUp()
        settle()
        capture("22b-fiche-grand-suite")
    }

    @MainActor
    private func launch(language: String, locale: String, extra: [String] = []) -> XCUIApplication {
        let app = XCUIApplication()
        app.launchArguments = ["-mira-reset", "-AppleLanguages", "(\(language))", "-AppleLocale", locale] + extra
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

    /// Waits for the stand-in Jellyfin to have received this request of « Watch together » (SyncPlay/<what>).
    private func waitForSync(_ what: String, timeout: TimeInterval) -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        let url = URL(string: "http://\(Self.server)/Mock/SyncPlay")!
        while Date() < deadline {
            if let data = try? Data(contentsOf: url), let answer = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
               let requests = answer["requests"] as? [[String: Any]], requests.contains(where: { $0["what"] as? String == what }) {
                return true
            }
            Thread.sleep(forTimeInterval: 0.5)
        }
        return false
    }

    /// Pictures fade in from the server: a moment before a capture.
    /// Settings is a long list, drawn as it scrolls: Sign out, at the bottom, exists only once scrolled to.
    @MainActor
    private func signOutButton(_ app: XCUIApplication) -> XCUIElement {
        let button = app.buttons["settings.signout"]
        for _ in 0..<8 where !(button.exists && button.isHittable) { app.swipeUp() }
        return button
    }

    private func settle() { Thread.sleep(forTimeInterval: 2.5) }

    @MainActor
    private func capture(_ name: String) {
        let attachment = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
        attachment.name = name
        attachment.lifetime = .keepAlways
        add(attachment)
    }
}
