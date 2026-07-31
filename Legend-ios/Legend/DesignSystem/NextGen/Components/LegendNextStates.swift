import SwiftUI

struct LegendNextEmptyState<Action: View>: View {
    let title: String
    let message: String
    let systemImage: String
    private let action: Action

    init(
        title: String,
        message: String,
        systemImage: String,
        @ViewBuilder action: () -> Action
    ) {
        self.title = title
        self.message = message
        self.systemImage = systemImage
        self.action = action()
    }

    var body: some View {
        LegendNextSurface(
            style: .elevated,
            cornerRadius: LegendNextRadius.prominentCard,
            padding: LegendNextSpacing.xl
        ) {
            VStack(spacing: LegendNextSpacing.md) {
                Image(systemName: systemImage)
                    .font(.system(size: 28, weight: .semibold))
                    .foregroundStyle(LegendNextColor.gold)
                    .frame(width: 58, height: 58)
                    .background(
                        LegendNextColor.gold.opacity(0.11),
                        in: Circle()
                    )
                    .accessibilityHidden(true)

                VStack(spacing: LegendNextSpacing.xs) {
                    Text(title)
                        .font(LegendNextTypography.section)
                        .foregroundStyle(LegendNextColor.textPrimary)
                        .multilineTextAlignment(.center)

                    Text(message)
                        .font(LegendNextTypography.body)
                        .foregroundStyle(LegendNextColor.textSecondary)
                        .multilineTextAlignment(.center)
                        .fixedSize(horizontal: false, vertical: true)
                }

                action
            }
            .frame(maxWidth: .infinity)
        }
        .accessibilityElement(children: .contain)
    }
}

extension LegendNextEmptyState where Action == EmptyView {
    init(
        title: String,
        message: String,
        systemImage: String
    ) {
        self.init(
            title: title,
            message: message,
            systemImage: systemImage
        ) {
            EmptyView()
        }
    }
}

struct LegendNextErrorState: View {
    let title: String
    let message: String
    let retryTitle: String?
    let retry: (() -> Void)?

    init(
        title: String,
        message: String,
        retryTitle: String? = nil,
        retry: (() -> Void)? = nil
    ) {
        self.title = title
        self.message = message
        self.retryTitle = retryTitle
        self.retry = retry
    }

    var body: some View {
        LegendNextSurface(style: .elevated) {
            VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
                HStack(alignment: .top, spacing: LegendNextSpacing.sm) {
                    Image(systemName: "exclamationmark.triangle.fill")
                        .font(.system(size: 17, weight: .semibold))
                        .foregroundStyle(LegendNextColor.danger)
                        .frame(width: 38, height: 38)
                        .background(
                            LegendNextColor.danger.opacity(0.10),
                            in: Circle()
                        )
                        .accessibilityHidden(true)

                    VStack(alignment: .leading, spacing: LegendNextSpacing.micro) {
                        Text(title)
                            .font(LegendNextTypography.cardTitle)
                            .foregroundStyle(LegendNextColor.textPrimary)

                        Text(message)
                            .font(LegendNextTypography.supporting)
                            .foregroundStyle(LegendNextColor.textSecondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }

                if let retryTitle, let retry {
                    Button(retryTitle, action: retry)
                        .buttonStyle(
                            LegendNextButtonStyle(kind: .secondary)
                        )
                }
            }
        }
        .accessibilityElement(children: .contain)
    }
}
