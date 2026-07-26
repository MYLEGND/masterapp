import SwiftUI

struct LegendLaunchView: View {
    var body: some View {
        ZStack {
            LegendPalette.primaryNavy
                .ignoresSafeArea()

            VStack(spacing: LegendSpacing.md) {
                LegendBrandLogo(maximumWidth: 124)
                    .accessibilityHidden(true)
                Text("Legend")
                    .font(.system(.title2, design: .rounded).weight(.bold))
                    .foregroundStyle(.white)
            }
            .accessibilityElement(children: .combine)
            .accessibilityLabel("Legend")
        }
    }
}
