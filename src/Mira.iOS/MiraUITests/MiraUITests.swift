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
        tapCard(app, poster)
        XCTAssertTrue(app.descendants(matching: .any)["title.name"].waitForExistence(timeout: 10), "The title page did not open")
        settle()
        capture("04-title")
        XCTAssertTrue(app.buttons["title.play"].exists, "No Play button on the title page")

        // Play: Mira's player over the app, sideways, the file as it is; Jellyfin hears where it is, then where it
        // stopped, once the player reduced over the tabs is closed.
        app.buttons["title.play"].tap()
        XCUIDevice.shared.orientation = .landscapeLeft
        XCTAssertTrue(waitForReport("/Start", item: Self.tearsOfSteel, timeout: 30), "Jellyfin never heard that playback started")
        Thread.sleep(forTimeInterval: 1.5)
        capture("05-player")
        closePlayer(app)
        XCTAssertTrue(waitForReport("/Stopped", item: Self.tearsOfSteel, timeout: 20), "Jellyfin never heard that playback stopped")
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

        // Watch together, from « … »: a group made here, the title put on for it from Play; Jellyfin's socket tells the
        // player when to start, and it says it is ready; then out of the group.
        let titleMore = app.buttons["title.more"]
        XCTAssertTrue(titleMore.waitForExistence(timeout: 5), "The title page has no « … »")
        titleMore.tap()
        // A menu's item, by its identifier or, if iOS does not pass it on, by its name.
        let together = app.buttons.matching(NSPredicate(format: "identifier == 'title.together' OR label == 'Watch together'")).firstMatch
        XCTAssertTrue(together.waitForExistence(timeout: 5), "« … » has no Watch together")
        settle()
        capture("05a-title-more")
        together.tap()
        let create = app.buttons["together.create"]
        XCTAssertTrue(create.waitForExistence(timeout: 5), "Watch together has no Create a group")
        settle()
        capture("05a-together-sheet")
        create.tap()
        let group = app.descendants(matching: .any)["together.group"]
        XCTAssertTrue(group.waitForExistence(timeout: 15), "Making a group did not show it on the title page")
        settle()
        capture("05b-together")
        let play = app.buttons["title.play"]
        for _ in 0..<4 where !play.isHittable { app.swipeDown() }
        XCTAssertEqual(play.label, "Start for the group")
        play.tap()
        XCUIDevice.shared.orientation = .landscapeLeft
        XCTAssertTrue(waitForSync("SetNewQueue", timeout: 30), "Play did not put the title on for the group")
        XCTAssertTrue(waitForSync("Ready", timeout: 30), "The player never told the group it was ready")
        Thread.sleep(forTimeInterval: 2.5)
        capture("05c-together-player")
        closePlayer(app)
        XCTAssertTrue(waitForSync("SetIgnoreWait", timeout: 20), "Closing the player did not tell the group to go on without it")
        let leave = app.buttons["together.leave"]
        for _ in 0..<4 where !leave.isHittable { app.swipeUp() }
        XCTAssertTrue(leave.waitForExistence(timeout: 10), "No Leave the group")
        leave.tap()
        XCTAssertTrue(group.waitForNonExistence(timeout: 15), "Leaving the group did not take it off the title page")
        app.navigationBars.buttons.firstMatch.tap()

        // A series: Play for its next episode, then its episodes, each with a Play of its own.
        let series = app.buttons["Pioneer One, 2010"].firstMatch
        XCTAssertTrue(series.waitForExistence(timeout: 10), "No card for Pioneer One")
        tapCard(app, series)
        // Its summary comes from Jellyfin in HTML: shown as text, cut at four lines, « More » opens it.
        let more = app.buttons["overview.more"]
        XCTAssertTrue(more.waitForExistence(timeout: 10), "A long summary is not cut with More")
        XCTAssertEqual(app.staticTexts.containing(NSPredicate(format: "label CONTAINS '<br>' OR label CONTAINS '&nbsp;'")).count, 0,
                       "The summary shows its HTML")
        settle()
        capture("06a-series-summary")
        more.tap()
        XCTAssertTrue(more.waitForNonExistence(timeout: 5), "More stays once the summary is open")
        // The episodes come after the summary, drawn as they scroll into view.
        let third = app.buttons["episode.play.3"]
        for _ in 0..<4 where !third.waitForExistence(timeout: 4) { app.swipeUp() }
        settle()
        capture("06-series")
        XCTAssertTrue(third.exists, "The series page lists no episodes")
        app.navigationBars.buttons.firstMatch.tap()

        // Scrolling down Home folded Mira's tab bar to its Home tab; back at the top, it shows every tab again.
        app.swipeDown()
        app.swipeDown()
        let movies = app.buttons["tab.movies"]
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
        app.buttons["tab.search"].tap()
        let field = app.searchFields.firstMatch
        XCTAssertTrue(field.waitForExistence(timeout: 10), "Search has no search field")
        field.tap()
        field.typeText("man")
        XCTAssertTrue(app.buttons.matching(NSPredicate(format: "label CONTAINS 'The Man from Mars'")).firstMatch.waitForExistence(timeout: 15),
                      "Search did not find The Man from Mars")
        settle()
        capture("08-search")
        // Out of the search field: the tab bar comes back in place of the keyboard (scrolling the results puts it away).
        field.typeText("\n")
        let settings = app.buttons["tab.settings"]
        if !settings.waitForExistence(timeout: 3) { app.swipeDown() }
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
        // The wrong password may still be in the field (iOS clears a secure field only when it gets the keyboard back).
        password.typeText(String(repeating: XCUIKeyboardKey.delete.rawValue, count: 12) + "mira\n")
        XCTAssertTrue(app.staticTexts["Continuer à regarder"].waitForExistence(timeout: 20), "L'Accueil n'a pas montré Continuer à regarder")
        dismissSavePassword(app)
        settle()
        capture("13-accueil")
        let poster = app.buttons["Tears of Steel, 2012"].firstMatch
        XCTAssertTrue(poster.waitForExistence(timeout: 10), "Pas de carte pour Tears of Steel")
        tapCard(app, poster)
        XCTAssertTrue(app.descendants(matching: .any)["title.name"].waitForExistence(timeout: 10), "La fiche ne s'est pas ouverte")
        settle()
        capture("13b-fiche")
        app.swipeUp()
        settle()
        capture("13c-fiche-suite")
        app.navigationBars.buttons.firstMatch.tap()
        app.swipeDown()
        app.buttons["tab.settings"].tap()
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
        let settings = app.buttons["tab.settings"]
        XCTAssertTrue(settings.waitForExistence(timeout: 5), "Pas d'onglet Réglages en grand texte")
        settings.tap()
        settle()
        capture("24-reglages-grand")
        let films = app.buttons["tab.movies"]
        XCTAssertTrue(films.waitForExistence(timeout: 5), "Pas d'onglet Films en grand texte")
        films.tap()
        settle()
        capture("23-films-grand")
        let poster = app.buttons["Tears of Steel, 2012"].firstMatch
        XCTAssertTrue(poster.waitForExistence(timeout: 10), "Tears of Steel absent des films")
        tapCard(app, poster)
        XCTAssertTrue(app.descendants(matching: .any)["title.name"].waitForExistence(timeout: 10), "La fiche ne s'est pas ouverte")
        settle()
        capture("22-fiche-grand")
        app.swipeUp()
        settle()
        capture("22b-fiche-grand-suite")
    }

    /// Mira's own player, sideways, on Sintel (its trailer when ios.yml could fetch it, with its trickplay pictures):
    /// the controls, the menus, a double tap, what a finger holds, the padlock, then reduced over the tabs and closed.
    @MainActor
    func testPlayer() throws {
        let app = launch(language: "en", locale: "en_US")
        signIn(app, password: "mira")
        XCTAssertTrue(app.staticTexts["Continue watching"].waitForExistence(timeout: 20), "Home never showed Continue watching")
        dismissSavePassword(app)
        let movies = app.tabBars.buttons["Movies"]
        XCTAssertTrue(movies.waitForExistence(timeout: 5), "No Movies tab")
        movies.tap()
        let sintel = app.buttons["Sintel, 2010"].firstMatch
        XCTAssertTrue(sintel.waitForExistence(timeout: 15), "No card for Sintel")
        sintel.tap()
        let start = app.buttons["title.play"]
        XCTAssertTrue(start.waitForExistence(timeout: 10), "Sintel's page has no Play")
        start.tap()
        XCUIDevice.shared.orientation = .landscapeLeft
        XCTAssertTrue(waitForReport("/Start", item: Self.sintel, timeout: 30), "Jellyfin never heard that Sintel started")
        Thread.sleep(forTimeInterval: 2)

        // Paused, the controls stay.
        let play = app.buttons["player.play"]
        if !play.exists { tapPicture(app) }
        XCTAssertTrue(play.waitForExistence(timeout: 5), "The player shows no Play button")
        if play.label == "Pause" { play.tap() }
        settle()
        capture("31-player")

        // Speed, quality and the sleep timer; then audio and subtitles: Apple's menus, in glass.
        for (button, name) in [("player.more", "32-player-menu"), ("player.tracks", "33-player-tracks")] {
            let menu = app.buttons[button]
            XCTAssertTrue(menu.waitForExistence(timeout: 5), "No \(button) in the player")
            menu.tap()
            Thread.sleep(forTimeInterval: 1.5)
            capture(name)
            app.coordinate(withNormalizedOffset: CGVector(dx: 0.08, dy: 0.5)).tap()
            Thread.sleep(forTimeInterval: 1)
        }

        // A double tap on the right: 10 s forward, the circles of YouTube.
        app.coordinate(withNormalizedOffset: CGVector(dx: 0.86, dy: 0.5)).doubleTap()
        capture("34-player-skip")
        Thread.sleep(forTimeInterval: 1.5)

        // What a finger holds: the timeline and its thumbnail, the volume, twice as fast, the padlock.
        let showcase = app.buttons["player.showcase"]
        XCTAssertTrue(showcase.exists, "No showcase button in a debug build")
        for name in ["35-player-scrub", "36-player-volume", "37-player-fast", "38-player-locked"] {
            showcase.tap()
            Thread.sleep(forTimeInterval: name == "35-player-scrub" ? 2.5 : 1)
            capture(name)
        }
        showcase.tap()

        // Reduced: the bar over the tabs, upright; a tap opens the player again.
        let minimize = app.buttons["player.minimize"]
        XCTAssertTrue(minimize.waitForExistence(timeout: 5), "No Reduce in the player")
        minimize.tap()
        XCUIDevice.shared.orientation = .portrait
        let bar = app.descendants(matching: .any)["mini"]
        XCTAssertTrue(bar.waitForExistence(timeout: 10), "Reducing the player showed no bar over the tabs")
        settle()
        capture("39-player-reduced")
        app.buttons["mini.close"].tap()
        XCTAssertTrue(waitForReport("/Stopped", item: Self.sintel, timeout: 20), "Closing the bar did not stop Sintel")
    }

    /// Reduces Mira's player to the bar over the tabs, then closes it there.
    @MainActor
    private func closePlayer(_ app: XCUIApplication) {
        let minimize = app.buttons["player.minimize"]
        if !minimize.waitForExistence(timeout: 2) { tapPicture(app) }
        XCTAssertTrue(minimize.waitForExistence(timeout: 5), "The player shows no Reduce button")
        minimize.tap()
        XCUIDevice.shared.orientation = .portrait
        let close = app.buttons["mini.close"]
        XCTAssertTrue(close.waitForExistence(timeout: 10), "Reducing the player showed no bar over the tabs")
        close.tap()
    }

    /// A tap in the middle of the picture, with the controls hidden: they come back.
    @MainActor
    private func tapPicture(_ app: XCUIApplication) {
        app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5)).tap()
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
    private func dismissSavePassword(_ app: XCUIApplication, timeout: TimeInterval = 20) {
        let button = app.buttons.matching(NSPredicate(format: "label IN %@", ["Not Now", "Pas maintenant"])).firstMatch
        if button.waitForExistence(timeout: timeout) { button.tap() }
    }

    /// A card's touch, once « Save Password? » is out of its way: iOS may show it more than 20 s after Home.
    @MainActor
    private func tapCard(_ app: XCUIApplication, _ card: XCUIElement) {
        if !card.isHittable { dismissSavePassword(app, timeout: 5) }
        card.tap()
    }

    static let tearsOfSteel = "b2e1d4c3f6a75b8e9d0c1f2e3a4b5c6d"
    static let sintel = "a1f0c3d2e5b64a7f8c9d0e1f2a3b4c5d"

    /// Waits for the stand-in Jellyfin to have received this report of the player (/Start, /Progress, /Stopped) about
    /// this title (the stand-in keeps them from one test to the next: each test plays its own title).
    private func waitForReport(_ kind: String, item: String, timeout: TimeInterval) -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        let url = URL(string: "http://\(Self.server)/Mock/Reports")!
        while Date() < deadline {
            if let data = try? Data(contentsOf: url), let reports = try? JSONSerialization.jsonObject(with: data) as? [[String: Any]],
               reports.contains(where: { $0["kind"] as? String == kind && $0["item"] as? String == item }) {
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
