import SwiftUI

struct LegendBrandLogo: View {
    var maximumWidth: CGFloat = 220

    var body: some View {
        Image("LegendLogo")
            .resizable()
            .scaledToFit()
            .frame(maxWidth: maximumWidth)
            .accessibilityLabel("Legend")
    }
}
