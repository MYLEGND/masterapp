import SwiftUI

/// The single visual authority for compact, people-facing cards. Journey
/// recommendations deliberately use their own richer surface because they
/// communicate recommendation context rather than a contact identity.
struct LegendContactCard<Avatar: View, Action: View>: View {
    let displayName: String
    let nameStatus: String?
    let subtitle: String?
    let detail: String?
    let isVerified: Bool
    let avatar: Avatar
    let action: Action

    init(
        displayName: String,
        nameStatus: String? = nil,
        subtitle: String? = nil,
        detail: String? = nil,
        isVerified: Bool = false,
        @ViewBuilder avatar: () -> Avatar,
        @ViewBuilder action: () -> Action
    ) {
        self.displayName = displayName
        self.nameStatus = nameStatus
        self.subtitle = subtitle
        self.detail = detail
        self.isVerified = isVerified
        self.avatar = avatar()
        self.action = action()
    }

    var body: some View {
        HStack(spacing: LegendNextSpacing.sm) {
            avatar

            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 4) {
                    LegendVerifiedName(
                        displayName,
                        isVerified: isVerified,
                        font: LegendNextTypography.bodyEmphasis
                    )

                    if let nameStatus = normalized(nameStatus) {
                        Text(nameStatus)
                            .font(.caption.weight(.bold))
                            .foregroundStyle(LegendNextColor.success)
                            .lineLimit(1)
                    }
                }

                if let subtitle = normalized(subtitle) {
                    Text(subtitle)
                        .font(LegendNextTypography.supporting)
                        .foregroundStyle(LegendNextColor.textSecondary)
                        .lineLimit(1)
                }

                if let detail = normalized(detail) {
                    Text(detail)
                        .font(LegendNextTypography.caption)
                        .foregroundStyle(LegendNextColor.textTertiary)
                        .lineLimit(1)
                }
            }

            Spacer(minLength: LegendNextSpacing.xs)

            action
        }
        .padding(.horizontal, LegendNextSpacing.sm)
        .padding(.vertical, LegendNextSpacing.xs)
        .frame(maxWidth: .infinity, minHeight: 64, alignment: .leading)
        .background(
            LegendNextColor.contactFill(for: colorScheme),
            in: RoundedRectangle(
                cornerRadius: LegendNextRadius.control,
                style: .continuous
            )
        )
        .overlay {
            RoundedRectangle(
                cornerRadius: LegendNextRadius.control,
                style: .continuous
            )
            .strokeBorder(LegendNextColor.contactBorder.opacity(0.82), lineWidth: 1)
        }
        .shadow(
            color: LegendNextColor.ambientShadow(for: colorScheme),
            radius: 7,
            y: 3
        )
        .contentShape(Rectangle())
    }

    @Environment(\.colorScheme) private var colorScheme

    private func normalized(_ value: String?) -> String? {
        guard let value = value?.trimmingCharacters(in: .whitespacesAndNewlines),
              !value.isEmpty else {
            return nil
        }
        return value
    }
}

struct LegendVerifiedName: View {
    let displayName: String
    let isVerified: Bool
    let font: Font
    let textColor: Color

    init(
        _ displayName: String,
        isVerified: Bool,
        font: Font = LegendNextTypography.bodyEmphasis,
        textColor: Color = LegendNextColor.textPrimary
    ) {
        self.displayName = displayName
        self.isVerified = isVerified
        self.font = font
        self.textColor = textColor
    }

    var body: some View {
        HStack(spacing: 4) {
            Text(displayName)
                .font(font)
                .foregroundStyle(textColor)
                .lineLimit(1)

            if isVerified {
                LegendVerifiedBadge()
            }
        }
        .accessibilityElement(children: .combine)
        .accessibilityLabel(isVerified ? "\(displayName), verified" : displayName)
    }
}

struct LegendVerifiedBadge: View {
    var body: some View {
        Image(systemName: "checkmark.seal.fill")
            .font(.caption.weight(.bold))
            .foregroundStyle(LegendNextColor.verified)
            .accessibilityLabel("Verified")
    }
}
