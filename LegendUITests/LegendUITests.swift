import XCTest

final class LegendUITests: XCTestCase {
    func testUnconfiguredBuildExplainsWhyItCannotConnect() {
        let app = XCUIApplication()
        app.launch()

        XCTAssertTrue(app.staticTexts["Mobile access requires server configuration"].waitForExistence(timeout: 5))
        XCTAssertFalse(app.staticTexts["No conversations"].exists)
    }
}
