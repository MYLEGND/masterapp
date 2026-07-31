import SwiftUI

struct LegendNextAvatar: View {
    let imageURL: URL?
    let initials: String
    let size: CGFloat
    let status: LegendNextAvatarStatus
    let accessibilityName: String?

    init(
        imageURL: URL? = nil,
        initials: String,
        size: CGFloat = LegendNextSize.avatarMedium,
        status: LegendNextAvatarStatus = .none,
        accessibilityName: String? = nil
    ) {
        self.imageURL = imageURL
        self.initials = initials
        self.size = size
        self.status = status
        self.accessibilityName = accessibilityName
    }

    var body: some View {
        ZStack(alignment: .bottomTrailing) {
            avatar
                .frame(width: size, height: size)
                .clipShape(Circle())
                .overlay {
                    Circle()
                        .strokeBorder(
                            Color.white.opacity(0.72),
                            lineWidth: max(1, size * 0.025)
                        )
                }
                .shadow(
                    color: LegendNextColor.navy.opacity(0.12),
                    radius: max(4, size * 0.10),
                    y: max(2, size * 0.04)
                )

            if status != .none {
                Circle()
                    .fill(statusColor)
                    .frame(
                        width: max(10, size * 0.24),
                        height: max(10, size * 0.24)
                    )
                    .overlay {
                        Circle()
                            .stroke(Color(uiColor: .systemBackground), lineWidth: 2)
                    }
                    .accessibilityHidden(true)
            }
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(accessibilityLabel)
    }

    @ViewBuilder
    private var avatar: some View {
        if let imageURL {
            AsyncImage(url: imageURL) { phase in
                switch phase {
                case let .success(image):
                    image
                        .resizable()
                        .scaledToFill()

                case .empty:
                    fallback
                        .overlay {
                            LegendSkeletonShape(cornerRadius: 999)
                                .opacity(0.55)
                        }

                case .failure:
                    fallback

                @unknown default:
                    fallback
                }
            }
        } else {
            fallback
        }
    }

    private var fallback: some View {
        ZStack {
            LegendNextGradient.hero

            Text(normalizedInitials)
                .font(
                    .system(
                        size: max(12, size * 0.34),
                        weight: .bold,
                        design: .rounded
                    )
                )
                .foregroundStyle(.white)
                .minimumScaleFactor(0.65)
                .lineLimit(1)
        }
    }

    private var normalizedInitials: String {
        let trimmed = initials
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .uppercased()

        return trimmed.isEmpty ? "L" : String(trimmed.prefix(3))
    }

    private var statusColor: Color {
        switch status {
        case .none:
            return .clear
        case .online:
            return LegendNextColor.success
        case .away:
            return LegendNextColor.warning
        case .busy:
            return LegendNextColor.danger
        }
    }

    private var accessibilityLabel: String {
        let name = accessibilityName?
            .trimmingCharacters(in: .whitespacesAndNewlines)

        let base = (name?.isEmpty == false) ? name! : normalizedInitials

        switch status {
        case .none:
            return base
        case .online:
            return "\(base), online"
        case .away:
            return "\(base), away"
        case .busy:
            return "\(base), busy"
        }
    }
}
