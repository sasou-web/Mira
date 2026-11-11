import Synchronization
import XCTest

/// The app on the simulator against the stand-in Jellyfin of tools/ios/mock-jellyfin.mjs (ios.yml starts it): sign in,
/// Home, a title, playing it, Settings, sign out, in English then in French; and Built-in Tailscale, from the sign-in
/// screen to a title played through it, on the stand-in Tailscale network of src/Mira.Tailscale (cmd/mira-testnet). Each
/// screen is captured (the attachments ios.yml takes out of the results), the only way to see the app without an iPhone.
final class MiraUITests: XCTestCase {
    static let server = "127.0.0.1:18097"

    override func setUp() {
        continueAfterFailure = false
    }

    /// The stand-in Jellyfin answering again as usual after each test: one stopped by a failure may leave it slow or
    /// away for the next. Nothing said when it isn't there (gallery.yml's real Jellyfin).
    override func tearDown() {
        var request = URLRequest(url: URL(string: "http://\(Self.server)/Mock/Up")!, timeoutInterval: 3)
        request.httpMethod = "POST"
        let done = DispatchSemaphore(value: 0)
        URLSession.shared.dataTask(with: request) { _, _, _ in done.signal() }.resume()
        _ = done.wait(timeout: .now() + 4)
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
        // Favorite, then not: Jellyfin is told each time, and the page reads it back. Only its glyph changes: the button
        // keeps its size and Play its place.
        let favorite = app.buttons["title.favorite"]
        XCTAssertTrue(favorite.waitForExistence(timeout: 5), "The title page has no Favorite")
        settle()
        let (playFrame, favoriteFrame) = (app.buttons["title.play"].frame, favorite.frame)
        favorite.tap()
        XCTAssertTrue(app.buttons.matching(NSPredicate(format: "identifier == 'title.favorite' AND label == 'In favorites'")).firstMatch.waitForExistence(timeout: 10),
                      "Favorite did not stick")
        settle()
        capture("04b-title-favorite")
        XCTAssertEqual(favorite.frame, favoriteFrame, "Favorite changed its size or its place")
        XCTAssertEqual(app.buttons["title.play"].frame, playFrame, "Favorite moved Play")
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
        // The group's button over the tabs, on every page while in a group: its sheet says who is in it, Invite, Leave.
        let groupButton = app.buttons["tab.group"]
        XCTAssertTrue(groupButton.waitForExistence(timeout: 5), "No group button over the tabs while in a group")
        groupButton.tap()
        let groupLeave = app.buttons["group.leave"]
        XCTAssertTrue(groupLeave.waitForExistence(timeout: 5), "The group button did not open the group's sheet")
        settle()
        capture("05b-group-sheet")
        app.buttons["group.close"].tap()
        XCTAssertTrue(groupLeave.waitForNonExistence(timeout: 5), "The group's sheet did not close")
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
        XCTAssertTrue(groupButton.waitForNonExistence(timeout: 10), "The group button stays over the tabs after leaving the group")
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
        // Scrolled: no header over the posters, only the sort button.
        app.swipeUp()
        settle()
        capture("07b-movies-scrolled")
        app.swipeDown()
        app.swipeDown()
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
        // Privacy: every connection out, with Built-in Tailscale's switch (PrivacyView).
        let privacy = app.buttons["settings.privacy"]
        for _ in 0..<5 where !(privacy.exists && privacy.isHittable) { app.swipeUp() }
        XCTAssertTrue(privacy.waitForExistence(timeout: 5), "No Privacy row in Settings")
        privacy.tap()
        XCTAssertTrue(app.navigationBars["Privacy"].waitForExistence(timeout: 10), "Privacy did not open")
        XCTAssertTrue(app.switches["privacy.tailscale"].waitForExistence(timeout: 5), "No Built-in Tailscale switch in Privacy")
        settle()
        capture("09c-privacy")
        app.navigationBars["Privacy"].buttons.firstMatch.tap()
        XCTAssertTrue(app.navigationBars["Settings"].waitForExistence(timeout: 10), "Privacy did not lead back to Settings")
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

    /// « Keep on this iPhone »: a film kept as its file (Tears of Steel), another as Jellyfin's conversion (Cosmos
    /// Laundromat, an MKV for the stand-in: its HLS segments put end to end); then Jellyfin away, « On this iPhone »
    /// plays the copy; Jellyfin back, it hears where the copy was left.
    @MainActor
    func testKeep() throws {
        let app = launch(language: "en", locale: "en_US")
        signIn(app, password: "mira")
        XCTAssertTrue(app.staticTexts["Continue watching"].waitForExistence(timeout: 20), "Home never showed Continue watching")
        dismissSavePassword(app)
        let movies = app.buttons["tab.movies"]
        for (name, shot) in [("Tears of Steel, 2012", "61-keep-file"), ("Cosmos Laundromat, 2015", "62-keep-converted")] {
            XCTAssertTrue(movies.waitForExistence(timeout: 5), "No Movies tab")
            movies.tap()
            let card = app.buttons[name].firstMatch
            XCTAssertTrue(card.waitForExistence(timeout: 15), "No card for \(name)")
            tapCard(app, card)
            let keep = app.buttons["title.keep"]
            XCTAssertTrue(keep.waitForExistence(timeout: 10), "\(name): no Keep on this iPhone")
            XCTAssertEqual(keep.label, "Keep on this iPhone")
            keep.tap()
            XCTAssertTrue(waitForLabel(keep, "On this iPhone", timeout: 90), "\(name) was never kept: \(keep.label)")
            settle()
            capture(shot)
        }

        // Jellyfin away (a PC switched off, far from home): the copy plays from this iPhone.
        mock("/Mock/Down")
        let before = reports("/UserData", item: Self.tearsOfSteel)
        app.buttons["tab.settings"].tap()
        let kept = app.buttons["settings.kept"]
        XCTAssertTrue(kept.waitForExistence(timeout: 10), "No On this iPhone row in Settings")
        kept.tap()
        XCTAssertTrue(app.navigationBars["On this iPhone"].waitForExistence(timeout: 10), "On this iPhone did not open")
        let rows = app.buttons.matching(identifier: "kept.row")
        XCTAssertTrue(rows.element(boundBy: 1).waitForExistence(timeout: 5), "On this iPhone does not list both films")
        settle()
        capture("63-kept-list")
        rows.matching(NSPredicate(format: "label CONTAINS 'Tears of Steel'")).firstMatch.tap()
        XCUIDevice.shared.orientation = .landscapeLeft
        let play = app.buttons["player.play"]
        XCTAssertTrue(reveal(play, app), "The copy opened no player")
        XCTAssertTrue(waitForLabel(play, "Pause", timeout: 15), "The copy does not play without Jellyfin: \(play.label)")
        Thread.sleep(forTimeInterval: 1.5)
        capture("64-kept-player")
        closePlayer(app)

        // Jellyfin back: told where the copy was left, once Mira comes back on screen.
        mock("/Mock/Up")
        XCUIDevice.shared.press(.home)
        Thread.sleep(forTimeInterval: 2)
        app.activate()
        XCTAssertTrue(waitForReport("/UserData", item: Self.tearsOfSteel, timeout: 40, after: before), "Jellyfin never heard where the copy was left")
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
        let movies = app.buttons["tab.movies"]
        XCTAssertTrue(movies.waitForExistence(timeout: 5), "No Movies tab")
        movies.tap()
        let sintel = app.buttons["Sintel, 2010"].firstMatch
        XCTAssertTrue(sintel.waitForExistence(timeout: 15), "No card for Sintel")
        tapCard(app, sintel)
        let start = app.buttons["title.play"]
        XCTAssertTrue(start.waitForExistence(timeout: 10), "Sintel's page has no Play")
        start.tap()
        XCUIDevice.shared.orientation = .landscapeLeft
        XCTAssertTrue(waitForReport("/Start", item: Self.sintel, timeout: 30), "Jellyfin never heard that Sintel started")
        Thread.sleep(forTimeInterval: 2)

        // Paused, the controls stay.
        let play = app.buttons["player.play"]
        XCTAssertTrue(reveal(play, app), "The player shows no Play button")
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

        // A double tap on the right: 10 s forward, its pill on that side.
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
        // Home scrolled down: the tab bar folds to its open tab, the reduced player beside it; back at the top, over it.
        app.buttons["tab.home"].tap()
        app.swipeUp()
        settle()
        XCTAssertTrue(bar.exists, "The reduced player left as the tab bar folded")
        capture("39b-player-folded")
        let close = app.buttons["mini.close"]
        for _ in 0..<3 where !close.waitForExistence(timeout: 3) { app.swipeDown() }
        close.tap()
        XCTAssertTrue(waitForReport("/Stopped", item: Self.sintel, timeout: 20), "Closing the bar did not stop Sintel")
    }

    /// Mira's opening (App/Opening.swift), as Mira opens every day, signed in: Jellyfin slow to answer, the opening gives
    /// way to Home once drawn; Jellyfin away (every connection cut, a PC switched off), it gives way at once to the Home
    /// kept from the last time, which says it can't reach Jellyfin; Jellyfin back, Retry brings Home.
    @MainActor
    func testOpening() throws {
        let app = launch(language: "en", locale: "en_US")
        signIn(app, password: "mira")
        XCTAssertTrue(app.staticTexts["Continue watching"].waitForExistence(timeout: 20), "Home never showed Continue watching")
        dismissSavePassword(app)

        mock("/Mock/Slow?ms=1500")
        var started = Date()
        let slow = launch(language: "en", locale: "en_US", reset: false)
        // XCTest may wait for the app to be idle, the opening gone: said, for what the timings mean.
        let opening = slow.descendants(matching: .any)["opening"]
        print("MIRA-OPENING slow: launched after \(Date().timeIntervalSince(started)) s, opening shown: \(opening.exists)")
        XCTAssertTrue(opening.waitForNonExistence(timeout: 20), "The opening never gave way to Home")
        print("MIRA-OPENING slow: gone after \(Date().timeIntervalSince(started)) s")
        XCTAssertTrue(slow.staticTexts["Continue watching"].exists, "The opening gave way to no Home")
        XCTAssertFalse(slow.buttons["Retry"].exists, "Home says Jellyfin can't be reached though it answered")
        settle()
        capture("51-opening-home")

        mock("/Mock/Down")
        started = Date()
        let away = launch(language: "en", locale: "en_US", reset: false)
        XCTAssertTrue(away.descendants(matching: .any)["opening"].waitForNonExistence(timeout: 20), "The opening never left with Jellyfin away")
        print("MIRA-OPENING away: gone after \(Date().timeIntervalSince(started)) s")
        let retry = away.buttons["Retry"]
        XCTAssertTrue(retry.waitForExistence(timeout: 30), "The Home kept from before does not say Jellyfin can't be reached")
        XCTAssertTrue(away.staticTexts["Continue watching"].exists, "The Home kept from before is not shown")
        settle()
        capture("52-opening-away")

        mock("/Mock/Up")
        retry.tap()
        XCTAssertTrue(retry.waitForNonExistence(timeout: 20), "Retry did not bring Home back from Jellyfin")
    }

    /// Jellyfin forgets the session (signed out from its dashboard, the device removed): the next screen that asks it
    /// something, here the recap in Settings, brings back the sign-in screen saying why, never « Wrong username or
    /// password. » under a screen that keeps a dead session; signing in again brings Home back.
    @MainActor
    func testSessionEnded() throws {
        let app = launch(language: "en", locale: "en_US")
        signIn(app, password: "mira")
        XCTAssertTrue(app.staticTexts["Continue watching"].waitForExistence(timeout: 20), "Home never showed Continue watching")
        dismissSavePassword(app)
        let settings = app.buttons["tab.settings"]
        XCTAssertTrue(settings.waitForExistence(timeout: 5), "No Settings tab")
        settings.tap()
        XCTAssertTrue(app.navigationBars["Settings"].waitForExistence(timeout: 10), "Settings did not open")
        mock("/Mock/Revoke")
        let recap = app.buttons["settings.report"].firstMatch
        XCTAssertTrue(recap.waitForExistence(timeout: 5), "No Your recap in Settings")
        recap.tap()
        let address = app.textFields["login.address"]
        if !address.waitForExistence(timeout: 15) {
            capture("53-session-ended-kept")
            XCTFail("A request refused for the session did not end it: \(app.staticTexts.allElementsBoundByIndex.map(\.label))")
            return
        }
        let why = app.descendants(matching: .any).matching(NSPredicate(format: "label CONTAINS 'session on Jellyfin has ended'")).firstMatch
        XCTAssertTrue(why.waitForExistence(timeout: 5), "The sign-in screen does not say the session ended")
        XCTAssertFalse(app.staticTexts["Wrong username or password."].exists, "An ended session is said as a wrong password")
        settle()
        capture("53-session-ended")
        app.buttons["login.continue"].tap()
        signInAccount(app, password: "mira")
        XCTAssertTrue(app.staticTexts["Continue watching"].waitForExistence(timeout: 20), "Signing in again did not bring Home back")
    }

    /// The film of Mira's opening (ios.yml's film: testOpeningFilm records the simulator meanwhile; skipped otherwise):
    /// opened with Jellyfin slow, quick, then away and back, between the marks ios.yml cuts the film at.
    @MainActor
    func testOpeningFilm() throws {
        guard ProcessInfo.processInfo.environment["MIRA_FILM"] == "1" else { throw XCTSkip("Only for ios.yml's film") }
        let app = launch(language: "en", locale: "en_US")
        signIn(app, password: "mira")
        XCTAssertTrue(app.staticTexts["Continue watching"].waitForExistence(timeout: 20), "Home never showed Continue watching")
        dismissSavePassword(app)
        settle()
        // From the Home Screen each time, as a person opens Mira: Jellyfin slow, then quick.
        @MainActor func fromHomeScreen() -> XCUIApplication {
            XCUIDevice.shared.press(.home)
            Thread.sleep(forTimeInterval: 1.2)
            return launch(language: "en", locale: "en_US", reset: false)
        }
        XCUIDevice.shared.press(.home)
        Thread.sleep(forTimeInterval: 1)
        mock("/Mock/Mark?name=film-start")
        for answer in ["/Mock/Slow?ms=1200", "/Mock/Up"] {
            mock(answer)
            let opened = fromHomeScreen()
            XCTAssertTrue(opened.descendants(matching: .any)["opening"].waitForNonExistence(timeout: 20), "The opening never gave way to Home")
            Thread.sleep(forTimeInterval: 2.5)
        }
        // Jellyfin away, then back: Retry.
        mock("/Mock/Down")
        let away = fromHomeScreen()
        let retry = away.buttons["Retry"]
        XCTAssertTrue(retry.waitForExistence(timeout: 30), "Home does not say Jellyfin can't be reached")
        Thread.sleep(forTimeInterval: 2.5)
        mock("/Mock/Up")
        retry.tap()
        XCTAssertTrue(retry.waitForNonExistence(timeout: 20), "Retry did not bring Home back from Jellyfin")
        Thread.sleep(forTimeInterval: 2)
        mock("/Mock/Mark?name=film-end")
    }

    /// A friend's party on another Jellyfin, with no account on this iPhone: its link opened in Mira asks the first
    /// name, makes this iPhone the party's guest (the stand-in's /Mira/guest), waits for the host's group, joins it as
    /// soon as the host opens it, then leaves it, back to the sign-in.
    @MainActor
    func testParty() throws {
        let app = launch(language: "en", locale: "en_US")
        XCTAssertTrue(app.textFields["login.address"].waitForExistence(timeout: 10), "No sign-in screen")
        let link = "mira://party/samsam-party-link-0001?server=" + "http://\(Self.server)".addingPercentEncoding(withAllowedCharacters: .alphanumerics)!
        app.open(URL(string: link)!)
        let heading = app.staticTexts["party.heading"]
        XCTAssertTrue(heading.waitForExistence(timeout: 15), "The party's screen did not open")
        XCTAssertTrue(waitForLabel(heading, "Join Sam’s party", timeout: 10), "The host is not named: \(heading.label)")
        let name = app.textFields["party.name"]
        focus(name)
        name.typeText("Léa")
        settle()
        capture("47-party-name")
        app.buttons["party.join"].tap()
        XCTAssertTrue(waitForLabel(heading, "Sam hasn’t started movie night yet", timeout: 15), "Not waiting for the host: \(heading.label)")
        capture("48-party-waiting")
        // The host opens their group: this iPhone joins it within a few seconds.
        mock("/Mock/PartyGroup")
        XCTAssertTrue(waitForSync("GuestJoin", timeout: 20), "The guest never joined the host's group")
        XCTAssertTrue(waitForLabel(heading, "You’re in Sam’s party", timeout: 15), "Not in the group: \(heading.label)")
        XCTAssertTrue(app.descendants(matching: .any)["party.members"].waitForExistence(timeout: 5), "The group's members are not shown")
        settle()
        capture("49-party-in")
        app.buttons["party.leave"].tap()
        XCTAssertTrue(waitForSync("GuestLeave", timeout: 15), "The guest never left the group")
        XCTAssertTrue(waitForSync("GuestLogout", timeout: 15), "The guest's session was not ended")
        XCTAssertTrue(app.textFields["login.address"].waitForExistence(timeout: 10), "Leaving the party did not bring the sign-in back")
    }

    /// A person's page: from a title's cast and crew (its director first, an actor credited twice shown once), their
    /// photo, birth and titles; one of them opens its page; Search finds the person by their name too.
    @MainActor
    func testPerson() throws {
        let app = launch(language: "en", locale: "en_US")
        signIn(app, password: "mira")
        XCTAssertTrue(app.staticTexts["Continue watching"].waitForExistence(timeout: 20), "Home never showed Continue watching")
        dismissSavePassword(app)
        let poster = app.buttons["Tears of Steel, 2012"].firstMatch
        XCTAssertTrue(poster.waitForExistence(timeout: 10), "No card for Tears of Steel")
        tapCard(app, poster)
        XCTAssertTrue(app.descendants(matching: .any)["title.name"].waitForExistence(timeout: 10), "The title page did not open")
        let director = app.buttons["title.person.0"], actor = app.buttons["title.person.1"]
        XCTAssertTrue(director.waitForExistence(timeout: 10), "The title page has no cast and crew")
        for _ in 0..<5 where !actor.isHittable { app.swipeUp() }
        XCTAssertTrue(director.label.hasPrefix("Ian Hubert"), "The director is not first: \(director.label)")
        XCTAssertEqual(actor.label, "Derek de Lint, Thom")
        XCTAssertEqual(app.buttons.matching(NSPredicate(format: "label BEGINSWITH 'Derek de Lint'")).count, 1, "An actor credited twice shows twice")
        settle()
        capture("55-title-credits")

        actor.tap()
        let name = app.staticTexts["person.name"]
        XCTAssertTrue(name.waitForExistence(timeout: 10), "The person's page did not open")
        XCTAssertEqual(name.label, "Derek de Lint")
        XCTAssertTrue(app.staticTexts["Born July 15, 1950 · The Hague, Netherlands"].waitForExistence(timeout: 10), "No birth under the name")
        let title = app.descendants(matching: .any)["person.titles"].buttons["Tears of Steel, 2012"]
        XCTAssertTrue(title.waitForExistence(timeout: 10), "The person's page does not list their film")
        settle()
        capture("56-person")
        title.tap()
        XCTAssertTrue(app.descendants(matching: .any)["title.name"].waitForExistence(timeout: 10), "A title of the person's page did not open")

        // The tab bar folded by the scrolling: back up, it unfolds.
        for _ in 0..<3 { app.swipeDown() }
        app.buttons["tab.search"].tap()
        let field = app.searchFields.firstMatch
        XCTAssertTrue(field.waitForExistence(timeout: 10), "Search has no search field")
        field.tap()
        field.typeText("derek")
        let found = app.descendants(matching: .any)["search.people"].buttons["Derek de Lint"]
        XCTAssertTrue(found.waitForExistence(timeout: 15), "Search did not find Derek de Lint")
        settle()
        capture("57-search-people")
        found.tap()
        XCTAssertTrue(name.waitForExistence(timeout: 10), "The person found by Search did not open their page")
    }

    /// A series' missing and coming episodes (TheTVDB's virtual episodes, from the stand-in Jellyfin), then a film put in
    /// a collection there already and in a new one, from « … » of its page.
    @MainActor
    func testSeriesCalendarAndCollections() throws {
        let app = launch(language: "en", locale: "en_US")
        signIn(app, password: "mira")
        XCTAssertTrue(app.staticTexts["Continue watching"].waitForExistence(timeout: 20), "Home never showed Continue watching")
        dismissSavePassword(app)
        let series = app.buttons["Pioneer One, 2010"].firstMatch
        XCTAssertTrue(series.waitForExistence(timeout: 10), "No card for Pioneer One")
        tapCard(app, series)
        let calendar = app.buttons["series.calendar"]
        for _ in 0..<5 where !(calendar.exists && calendar.isHittable) { app.swipeUp() }
        XCTAssertTrue(calendar.waitForExistence(timeout: 10), "The series page says nothing of its missing episode")
        XCTAssertTrue(calendar.label.contains("1 missing episode: S1 · E4"), "Not the missing episode: \(calendar.label)")
        XCTAssertTrue(calendar.label.contains("Next episode: S1 · E5"), "Not the next episode: \(calendar.label)")
        calendar.tap()
        XCTAssertTrue(app.descendants(matching: .any)["series.calendar.list"].waitForExistence(timeout: 5), "The line did not open into its list")
        XCTAssertTrue(app.descendants(matching: .any).matching(NSPredicate(format: "label CONTAINS 'Triangle'")).firstMatch.exists, "The list does not name the missing episode")
        settle()
        capture("60-series-calendar")
        // An account that shows missing episodes in Jellyfin (the stand-in lists them so unless asked isMissing=false):
        // they are on this line only, never a season of their own nor rows to play (they play nothing).
        XCTAssertFalse(app.buttons["Season 2"].exists, "A season of missing episodes only is offered")
        let third = app.buttons["episode.play.3"]
        for _ in 0..<5 where !(third.exists && third.isHittable) { app.swipeUp() }
        app.swipeUp()
        XCTAssertTrue(third.exists, "The series page lists no third episode")
        XCTAssertFalse(app.buttons["episode.play.4"].exists || app.buttons["episode.play.5"].exists, "A missing episode is listed to play")
        app.navigationBars.buttons.firstMatch.tap()

        for _ in 0..<3 { app.swipeDown() }
        let poster = app.buttons["Tears of Steel, 2012"].firstMatch
        XCTAssertTrue(poster.waitForExistence(timeout: 10), "No card for Tears of Steel")
        tapCard(app, poster)
        let more = app.buttons["title.more"]
        XCTAssertTrue(more.waitForExistence(timeout: 10), "The title page has no « … »")
        more.tap()
        let add = app.buttons.matching(NSPredicate(format: "identifier == 'title.collection' OR label == 'Add to a collection…'")).firstMatch
        XCTAssertTrue(add.waitForExistence(timeout: 5), "« … » has no Add to a collection")
        add.tap()
        let existing = app.buttons["collection.row.Movie nights"]
        XCTAssertTrue(existing.waitForExistence(timeout: 10), "The sheet does not list the collection already there")
        existing.tap()
        let status = app.staticTexts["collection.status"]
        XCTAssertTrue(status.waitForExistence(timeout: 10), "Nothing said once the film was added")
        XCTAssertEqual(status.label, "Added to “Movie nights”.")
        let name = "Shorts \(Int(Date().timeIntervalSince1970))"
        let field = app.textFields["collection.name"]
        focus(field)
        field.typeText(name)
        app.buttons["collection.create"].tap()
        XCTAssertTrue(app.buttons["collection.row.\(name)"].waitForExistence(timeout: 10), "The new collection is not in the list")
        XCTAssertTrue(app.staticTexts.matching(NSPredicate(format: "label == %@", "Collection “\(name)” created with “Tears of Steel”.")).firstMatch.waitForExistence(timeout: 5),
                      "The new collection is not said")
        // The name of a collection there already is refused before Jellyfin sees it.
        focus(field)
        field.typeText("movie NIGHTS")
        app.buttons["collection.create"].tap()
        XCTAssertTrue(app.staticTexts.matching(NSPredicate(format: "label == %@", "This name is already taken by the collection “Movie nights”.")).firstMatch.waitForExistence(timeout: 5),
                      "A name taken was not refused")
        settle()
        capture("61-collections")
        app.buttons["collection.done"].tap()
        XCTAssertTrue(field.waitForNonExistence(timeout: 5), "Done did not close the sheet")
    }

    /// The README's and the site's captures of the app (gallery.yml, never ios.yml: skipped without MIRA_GALLERY_SERVER):
    /// signed in to a real Jellyfin 12.1 whose library is the README's demonstration one (tools/banner/media.sh, set up
    /// by tools/gallery/setup.mjs), in the language asked for: Home, the films, a film's page, a series' episodes, and a
    /// group made on the film's page. Named iphone-<screen>, the names gallery.yml keeps.
    @MainActor
    func testGallery() throws {
        let environment = ProcessInfo.processInfo.environment
        guard let server = environment["MIRA_GALLERY_SERVER"], !server.isEmpty else { throw XCTSkip("Only for gallery.yml") }
        let french = environment["MIRA_GALLERY_LANGUAGE"] == "fr"
        let app = launch(language: french ? "fr" : "en", locale: french ? "fr_FR" : "en_US")
        let address = app.textFields["login.address"]
        XCTAssertTrue(address.waitForExistence(timeout: 10), "No sign-in screen")
        focus(address)
        address.typeText(server)
        app.buttons["login.continue"].tap()
        signInAccount(app, user: environment["MIRA_GALLERY_USER"] ?? "", password: environment["MIRA_GALLERY_PASSWORD"] ?? "")
        XCTAssertTrue(app.staticTexts[french ? "Continuer à regarder" : "Continue watching"].waitForExistence(timeout: 30), "Home never showed Continue watching")
        dismissSavePassword(app)
        // The posters and backdrops come from the server, at their size in pixels: longer than the stand-in's. The
        // banner's picture waited for (once captured with its blurred placeholder, the README's first picture).
        Thread.sleep(forTimeInterval: 5)
        XCTAssertTrue(waitForSharpBanner(timeout: 60), "Home's banner still shows its blurred placeholder")
        capture("iphone-home")

        app.buttons["tab.movies"].tap()
        XCTAssertTrue(app.buttons.matching(NSPredicate(format: "label BEGINSWITH 'Coco'")).firstMatch.waitForExistence(timeout: 15), "Movies shows no grid of films")
        Thread.sleep(forTimeInterval: 5)
        capture("iphone-movies")

        app.buttons["tab.home"].tap()
        let film = app.buttons.matching(NSPredicate(format: "label BEGINSWITH 'Spider-Man: Across the Spider-Verse'")).firstMatch
        XCTAssertTrue(film.waitForExistence(timeout: 10), "No card for Spider-Man: Across the Spider-Verse on Home")
        tapCard(app, film)
        XCTAssertTrue(app.descendants(matching: .any)["title.name"].waitForExistence(timeout: 10), "The film's page did not open")
        Thread.sleep(forTimeInterval: 5)
        capture("iphone-film")
        // Watch together, from « … »: the group on the film's page, as a friend's invitation finds it.
        app.buttons["title.more"].tap()
        let together = app.buttons.matching(NSPredicate(format: "identifier == 'title.together' OR label == %@", french ? "Regarder ensemble" : "Watch together")).firstMatch
        XCTAssertTrue(together.waitForExistence(timeout: 5), "« … » has no Watch together")
        together.tap()
        let create = app.buttons["together.create"]
        XCTAssertTrue(create.waitForExistence(timeout: 5), "Watch together has no Create a group")
        create.tap()
        XCTAssertTrue(app.descendants(matching: .any)["together.group"].waitForExistence(timeout: 15), "Making a group did not show it on the film's page")
        settle()
        capture("iphone-together")
        let leave = app.buttons["together.leave"]
        for _ in 0..<4 where !leave.isHittable { app.swipeUp() }
        if leave.exists { leave.tap() }
        app.navigationBars.buttons.firstMatch.tap()

        // A series from Shows, scrolled to its episodes, the third one started.
        for _ in 0..<2 where !app.buttons["tab.shows"].exists { app.swipeDown() }
        app.buttons["tab.shows"].tap()
        let series = app.buttons.matching(NSPredicate(format: "label BEGINSWITH 'Arcane'")).firstMatch
        XCTAssertTrue(series.waitForExistence(timeout: 15), "No card for Arcane")
        tapCard(app, series)
        let third = app.buttons["episode.play.3"]
        for _ in 0..<5 where !(third.exists && third.isHittable) { app.swipeUp() }
        XCTAssertTrue(third.exists, "Arcane's page lists no episodes")
        Thread.sleep(forTimeInterval: 5)
        capture("iphone-series")
    }

    /// Built-in Tailscale from end to end (ios.yml starts the stand-in Tailscale network, whose « Jellyfin PC » puts the
    /// stand-in Jellyfin on it, and passes its address in MIRA_TS_CONTROL): turned on from the sign-in screen, signed in
    /// once on its page, Jellyfin found on the network, then the account, Home and a title played; every request through
    /// the node and its relay (`-mira-way-builtin`: never straight to the stand-in Jellyfin, though it is on this Mac).
    /// Then the PC switched off and on again, as Settings tells it. Last of the tests (by name): ios.yml reads its node's
    /// log in the app's folder, which the next test's reset would delete.
    @MainActor
    func testTailscale() throws {
        guard let control = ProcessInfo.processInfo.environment["MIRA_TS_CONTROL"], !control.isEmpty else {
            throw XCTSkip("No stand-in Tailscale network (MIRA_TS_CONTROL)")
        }
        let app = launch(language: "en", locale: "en_US", extra: ["-mira-tailscale-control", control, "-mira-way-builtin"])
        let entry = app.buttons["login.tailscale"]
        XCTAssertTrue(entry.waitForExistence(timeout: 10), "The sign-in screen offers no Built-in Tailscale")
        entry.tap()
        let turnOn = app.buttons["tailscale.turnOn"]
        XCTAssertTrue(turnOn.waitForExistence(timeout: 10), "Built-in Tailscale's sheet has no Turn on")
        settle()
        capture("41-tailscale")
        turnOn.tap()
        // Tailscale's page over Mira, the stand-in network's signing this iPhone in as it opens, closed by Mira once the
        // node runs. iOS asks first only for a page that shares Safari's cookies, which Mira's never does.
        let consent = XCUIApplication(bundleIdentifier: "com.apple.springboard").buttons["Continue"]
        if consent.waitForExistence(timeout: 3) { consent.tap() }
        let found = app.buttons["tailscale.server.0"]
        if !found.waitForExistence(timeout: 60) {
            capture("42-tailscale-not-found")
            // Looked for before the PC showed online: looked for again, as a person would.
            let again = app.buttons["Look again"]
            if again.exists { again.tap() }
            XCTAssertTrue(found.waitForExistence(timeout: 30), "Jellyfin was never found on the Tailscale network")
        }
        XCTAssertTrue(app.descendants(matching: .any)["tailscale.connected"].exists, "The sheet does not say Mira is connected to Tailscale")
        settle()
        capture("42-tailscale-found")
        // The server's row can sit under the bottom of the screen, below the outage note: brought up before the tap,
        // which would otherwise land on the screen's edge and open nothing.
        let screen = app.windows.firstMatch.frame
        for _ in 0..<4 where !(found.isHittable && found.frame.maxY < screen.maxY - 40) { app.swipeUp() }
        found.tap()
        signInAccount(app, password: "mira")
        XCTAssertTrue(app.staticTexts["Continue watching"].waitForExistence(timeout: 30), "Home never showed Continue watching through Built-in Tailscale")
        dismissSavePassword(app)
        settle()
        capture("43-tailscale-home")

        // A title played through the relay: Mira's player reads the file from it. Tears of Steel played in testEnglish
        // already: the reports of this play are those after the ones counted now.
        let started = reports("/Start", item: Self.tearsOfSteel), stopped = reports("/Stopped", item: Self.tearsOfSteel)
        let poster = app.buttons["Tears of Steel, 2012"].firstMatch
        XCTAssertTrue(poster.waitForExistence(timeout: 10), "No card for Tears of Steel")
        tapCard(app, poster)
        let play = app.buttons["title.play"]
        XCTAssertTrue(play.waitForExistence(timeout: 15), "The title page did not open")
        play.tap()
        // Paused as soon as it plays: the stand-in's clip lasts 10 s for a title of 12 min, and through the relay the
        // Start report could come late enough that the clip ended first (a cut: « Can't play this title », no Reduce
        // button; main, 2026-11-08). Jellyfin is told Start once the item is ready, paused or not.
        let pause = app.buttons["player.play"]
        let playing = NSPredicate(format: "label == %@", "Pause")
        XCTAssertTrue(XCTWaiter().wait(for: [XCTNSPredicateExpectation(predicate: playing, object: pause)], timeout: 45) == .completed, "The player never played through Built-in Tailscale")
        pause.tap()
        XCTAssertTrue(waitForReport("/Start", item: Self.tearsOfSteel, timeout: 45, after: started), "Jellyfin never heard through Built-in Tailscale that playback started")
        capture("44-tailscale-player")
        closePlayer(app)
        XCTAssertTrue(waitForReport("/Stopped", item: Self.tearsOfSteel, timeout: 20, after: stopped), "Jellyfin never heard through Built-in Tailscale that playback stopped")
        XCTAssertTrue(play.waitForExistence(timeout: 10), "Closing the player did not lead back to the title page")
        app.navigationBars.buttons.firstMatch.tap()

        // Settings: the way taken, the network, the PC; switched off, then on again. Home scrolled to the card folded
        // Mira's tab bar to its Home tab: scrolling back up unfolds it.
        let settings = app.buttons["tab.settings"]
        if !settings.waitForExistence(timeout: 3) { app.swipeDown() }
        XCTAssertTrue(settings.waitForExistence(timeout: 5), "No Settings tab")
        settings.tap()
        let way = app.descendants(matching: .any)["settings.way"]
        for _ in 0..<4 where !(way.exists && way.isHittable) { app.swipeUp() }
        XCTAssertTrue(way.waitForExistence(timeout: 10), "Settings does not say how Mira reaches Jellyfin")
        XCTAssertTrue(way.label.contains("Through Built-in Tailscale"), "The way to Jellyfin: \(way.label)")
        // The PC's row is above the way, past the outage setting and Sign out: scrolled to the way, the list may have
        // left it off screen, where a lazy list doesn't draw it. Back up a little until it is there.
        let online = app.staticTexts["PC with Jellyfin online"]
        for _ in 0..<4 where !online.waitForExistence(timeout: 3) { app.swipeDown(velocity: .slow) }
        XCTAssertTrue(online.waitForExistence(timeout: 15), "Settings does not show the PC with Jellyfin online")
        settle()
        capture("45-tailscale-settings")
        bench(control, "/mira/jellyfin?online=0")
        XCTAssertTrue(app.staticTexts["PC with Jellyfin offline"].waitForExistence(timeout: 45), "Settings never said the PC with Jellyfin is offline")
        settle()
        capture("46-tailscale-pc-off")
        bench(control, "/mira/jellyfin?online=1")
        XCTAssertTrue(app.staticTexts["PC with Jellyfin online"].waitForExistence(timeout: 45), "Settings never said the PC with Jellyfin is back online")
    }

    /// Reduces Mira's player to the bar over the tabs, then closes it there.
    @MainActor
    private func closePlayer(_ app: XCUIApplication) {
        // Paused first: the clip of the stand-in Jellyfin lasts 10 s for a title of 12 min, and a title that ends before
        // 90 % is a cut, which closes the player with « Can't play this title ».
        let play = app.buttons["player.play"]
        if reveal(play, app), play.label == "Pause" { play.tap() }
        let minimize = app.buttons["player.minimize"]
        XCTAssertTrue(reveal(minimize, app), "The player shows no Reduce button")
        minimize.tap()
        XCUIDevice.shared.orientation = .portrait
        settle()
        let bar = app.descendants(matching: .any)["mini"]
        XCTAssertTrue(bar.waitForExistence(timeout: 10), "Reducing the player showed no bar over the tabs")
        let close = app.buttons["mini.close"]
        for _ in 0..<2 where !close.waitForExistence(timeout: 3) { app.swipeDown() }
        XCTAssertTrue(close.waitForExistence(timeout: 5), "The reduced player has no close button")
        close.tap()
    }

    /// The controls hide while the title plays: a tap on the picture brings them back, `element` with them.
    @MainActor
    private func reveal(_ element: XCUIElement, _ app: XCUIApplication) -> Bool {
        for _ in 0..<3 {
            if element.waitForExistence(timeout: 1.5) { return true }
            tapPicture(app)
        }
        return element.waitForExistence(timeout: 2)
    }

    /// A tap in the middle of the picture, with the controls hidden: they come back.
    @MainActor
    private func tapPicture(_ app: XCUIApplication) {
        app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5)).tap()
    }

    /// Mira opened (closed first if it runs), from nothing (`reset`: no account, nothing kept) or as it was left.
    @MainActor
    private func launch(language: String, locale: String, extra: [String] = [], reset: Bool = true) -> XCUIApplication {
        let app = XCUIApplication()
        // The player's controls stay up: they hide 4 s after a touch, less than XCTest may take between finding a
        // button and tapping it while a video plays.
        app.launchArguments = (reset ? ["-mira-reset"] : []) + ["-mira-controls-stay", "-AppleLanguages", "(\(language))", "-AppleLocale", locale] + extra
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
        signInAccount(app, password: password)
    }

    /// The account's form, once the server is found.
    @MainActor
    private func signInAccount(_ app: XCUIApplication, user: String = "ogsas", password: String) {
        let username = app.textFields["login.username"]
        XCTAssertTrue(username.waitForExistence(timeout: 15), "The server was not found")
        focus(username)
        // Return goes on to the password, as a person would.
        username.typeText(user + "\n")
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
    /// this title, more than `after` of them (the stand-in keeps them from one test to the next: each test plays its own
    /// title, or counts those of the tests before).
    private func waitForReport(_ kind: String, item: String, timeout: TimeInterval, after: Int = 0) -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if reports(kind, item: item) > after { return true }
            Thread.sleep(forTimeInterval: 0.5)
        }
        return false
    }

    /// How many reports of this kind about this title the stand-in Jellyfin has received.
    private func reports(_ kind: String, item: String) -> Int {
        guard let data = try? Data(contentsOf: URL(string: "http://\(Self.server)/Mock/Reports")!),
              let reports = try? JSONSerialization.jsonObject(with: data) as? [[String: Any]] else { return 0 }
        return reports.count { $0["kind"] as? String == kind && $0["item"] as? String == item }
    }

    /// Tells the stand-in Tailscale network something (POST /mira/…: the Jellyfin PC off or on), and checks it was done.
    private func bench(_ control: String, _ command: String) {
        var request = URLRequest(url: URL(string: control + command)!)
        request.httpMethod = "POST"
        let answer = Answer()
        let done = DispatchSemaphore(value: 0)
        URLSession.shared.dataTask(with: request) { _, response, _ in
            answer.status.withLock { $0 = (response as? HTTPURLResponse)?.statusCode ?? 0 }
            done.signal()
        }.resume()
        _ = done.wait(timeout: .now() + 10)
        XCTAssertEqual(answer.status.withLock { $0 }, 204, "The stand-in Tailscale network did not take \(command)")
    }

    /// Waits for an element to say this.
    @MainActor
    private func waitForLabel(_ element: XCUIElement, _ label: String, timeout: TimeInterval) -> Bool {
        let said = XCTNSPredicateExpectation(predicate: NSPredicate(format: "label == %@", label), object: element)
        return XCTWaiter().wait(for: [said], timeout: timeout) == .completed
    }

    /// Tells the stand-in Jellyfin something (POST /Mock/…: the host opens their group), and checks it was done.
    private func mock(_ path: String) {
        bench("http://\(Self.server)", path)
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

    /// Waits for the picture at the top of Home's banner to be the picture itself: its blurred placeholder (a BlurHash
    /// of 24 × 24 drawn over the whole width) has almost no difference from one pixel to the next, a backdrop has
    /// plenty. Measured under the status bar, above the title's logo, where the banner's fade is clear.
    @MainActor
    private func waitForSharpBanner(timeout: TimeInterval) -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        var detail = 0.0
        while Date() < deadline {
            if let picture = XCUIScreen.main.screenshot().image.cgImage {
                detail = Self.detail(picture, from: 0.08, to: 0.3)
                if detail > 2 { return true }
            }
            Thread.sleep(forTimeInterval: 1)
        }
        print("Banner's detail: \(detail)")
        return false
    }

    /// The mean difference of brightness between neighbouring pixels (0 to 255) of a band of `picture`, given in
    /// fractions of its height, drawn at half its size.
    private static func detail(_ picture: CGImage, from top: Double, to bottom: Double) -> Double {
        let width = picture.width / 2, height = picture.height / 2
        var pixels = [UInt8](repeating: 0, count: width * height)
        let drawn = pixels.withUnsafeMutableBytes { buffer -> Bool in
            guard let context = CGContext(data: buffer.baseAddress, width: width, height: height, bitsPerComponent: 8, bytesPerRow: width,
                                          space: CGColorSpaceCreateDeviceGray(), bitmapInfo: CGImageAlphaInfo.none.rawValue) else { return false }
            context.interpolationQuality = .medium
            context.draw(picture, in: CGRect(x: 0, y: 0, width: width, height: height))
            return true
        }
        guard drawn, width > 1 else { return 0 }
        // Rows from the top of the picture (the context's memory starts at its top row).
        let first = Int(Double(height) * top), last = Int(Double(height) * bottom)
        var total = 0, count = 0
        for y in first..<max(first, last) {
            let row = y * width
            for x in 1..<width {
                total += abs(Int(pixels[row + x]) - Int(pixels[row + x - 1]))
                count += 1
            }
        }
        return count == 0 ? 0 : Double(total) / Double(count)
    }

    @MainActor
    private func capture(_ name: String) {
        let attachment = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
        attachment.name = name
        attachment.lifetime = .keepAlways
        add(attachment)
    }
}

/// What a request answered, written from URLSession's queue.
private final class Answer: Sendable {
    let status = Mutex(0)
}
