import XCTest

final class LegendUITests: XCTestCase {
    override func setUpWithError() throws {
        continueAfterFailure = false
    }

    func testGuestCanExploreAndReturnWithoutAuthentication() {
        let app = XCUIApplication()
        app.launch()
        let guest = app.buttons["Continue as guest"]
        XCTAssertTrue(guest.waitForExistence(timeout: 20))
        guest.tap()
        XCTAssertTrue(app.buttons["Sign in securely"].waitForExistence(timeout: 10))
        let image = XCTAttachment(screenshot: app.screenshot())
        image.name = "Guest exploration"
        image.lifetime = .keepAlways
        add(image)
        app.buttons["Back"].firstMatch.tap()
        XCTAssertTrue(guest.waitForExistence(timeout: 10))
    }

    func testApplicationLaunches() {
        let app = XCUIApplication()
        app.launch()
        XCTAssertTrue(app.exists)
    }
}
