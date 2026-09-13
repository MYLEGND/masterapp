import SwiftUI

struct LegendBrandLogo: View {
    var maximumWidth: CGFloat = 220

    var body: some View {
        Image("LegendLogo")
            .resizable()
            .scaledToFit()
            .frame(maxWidth: maximumWidth)
            .accessibilityLabel(LegendLocalized("LEGEND®", context: "accessibility copy"))
    }
}

/// The application shell and call window render the same stationary wordmark.
struct LegendAppWordmark: View {
    var color: Color
    var body: some View {
        Text(LegendLocalized("LEGEND"))
                .font(LegendNextTypography.wordmark)
                .tracking(LegendSharedDesign.tracking("wordmark"))
                .foregroundStyle(Color.clear)
                .overlay(alignment: .leading) {
                    Text(LegendLocalized("LEGEND®"))
                        .font(LegendNextTypography.wordmark)
                        .tracking(
                            LegendSharedDesign.tracking("wordmark")
                        )
                        .foregroundStyle(color)
                        .fixedSize(
                            horizontal: true,
                            vertical: false
                        )
                        .allowsHitTesting(false)
                        .accessibilityHidden(true)
                }
                .frame(maxWidth: .infinity)
                .accessibilityLabel(LegendLocalized("LEGEND registered", context: "accessibility copy"))
                .accessibilityAddTraits(.isHeader)
    }
}
