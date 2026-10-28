import XCTest

/// The app on the simulator against the stand-in Jellyfin of tools/ios/mock-jellyfin.mjs (ios.yml starts it): sign in,
/// Home, a title, Settings, sign out, in English then in French. Each screen is captured (the attachments ios.yml
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
        app.navigationBars.buttons.firstMatch.tap()

        app.tabBars.buttons["Settings"].tap()
        XCTAssertTrue(app.buttons["settings.signout"].waitForExistence(timeout: 10))
        capture("05-settings")
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
        password.tap()
        password.typeText("mira\n")
        dismissSavePassword(app)
        XCTAssertTrue(app.staticTexts["Continuer à regarder"].waitForExistence(timeout: 20), "L'Accueil n'a pas montré Continuer à regarder")
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
        address.tap()
        address.typeText(Self.server)
        app.buttons["login.continue"].tap()
        let username = app.textFields["login.username"]
        XCTAssertTrue(username.waitForExistence(timeout: 15), "The server was not found")
        username.tap()
        username.typeText("ogsas")
        let field = app.secureTextFields["login.password"]
        field.tap()
        field.typeText(password)
        app.buttons["login.signin"].tap()
        if password == "mira" { dismissSavePassword(app) }
    }

    /// iOS may offer to save the password: not now.
    @MainActor
    private func dismissSavePassword(_ app: XCUIApplication) {
        for name in ["Not Now", "Pas maintenant"] {
            let button = app.buttons[name]
            if button.waitForExistence(timeout: 2) { button.tap(); return }
        }
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
