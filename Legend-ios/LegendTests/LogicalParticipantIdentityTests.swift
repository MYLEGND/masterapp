import XCTest
@testable import Legend

final class LogicalParticipantIdentityTests: XCTestCase {
    func testSameUserIDWithDifferentParticipantTypesRemainDistinct() throws {
        let agent = try LogicalParticipantIdentity(userID: " Same-User ", participantType: .agent)
        let client = try LogicalParticipantIdentity(userID: "same-user", participantType: .client)

        XCTAssertNotEqual(agent, client)
        XCTAssertEqual(agent.userID, "same-user")
        XCTAssertEqual(Set([agent, client]).count, 2)
    }

    func testDecodeNormalizesUserIDButPreservesParticipantType() throws {
        let data = """
        { "userId": "  SAME-USER ", "participantType": "Client" }
        """.data(using: .utf8)!

        let identity = try JSONDecoder.mobile.decode(LogicalParticipantIdentity.self, from: data)

        XCTAssertEqual(identity.userID, "same-user")
        XCTAssertEqual(identity.participantType, .client)
    }

    func testEmptyUserIDIsRejected() {
        XCTAssertThrowsError(try LogicalParticipantIdentity(userID: "   ", participantType: .agent))
    }
}
