import SwiftUI

extension View {
    func legendNextShimmer(
        active: Bool = true,
        duration: Double = 1.25
    ) -> some View {
        modifier(
            LegendNextShimmerModifier(
                active: active,
                duration: duration
            )
        )
    }
}

private struct LegendNextShimmerModifier: ViewModifier {
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    let active: Bool
    let duration: Double

    @State private var phase: CGFloat = -1.2

    func body(content: Content) -> some View {
        content
            .overlay {
                if active && !reduceMotion {
                    GeometryReader { proxy in
                        LinearGradient(
                            colors: [
                                .clear,
                                .white.opacity(0.28),
                                .clear
                            ],
                            startPoint: .top,
                            endPoint: .bottom
                        )
                        .frame(width: max(80, proxy.size.width * 0.42))
                        .rotationEffect(.degrees(18))
                        .offset(
                            x: phase * (proxy.size.width + 160)
                        )
                        .blendMode(.screen)
                    }
                    .allowsHitTesting(false)
                    .mask(content)
                }
            }
            .onAppear {
                startAnimationIfNeeded()
            }
            .onChange(of: active) { _, isActive in
                if isActive {
                    startAnimationIfNeeded()
                }
            }
    }

    private func startAnimationIfNeeded() {
        guard active, !reduceMotion else {
            return
        }

        phase = -1.2

        withAnimation(
            .linear(duration: duration)
            .repeatForever(autoreverses: false)
        ) {
            phase = 1.2
        }
    }
}

struct LegendNextSkeletonLine: View {
    let width: CGFloat?
    let height: CGFloat

    init(
        width: CGFloat? = nil,
        height: CGFloat = 12
    ) {
        self.width = width
        self.height = height
    }

    var body: some View {
        RoundedRectangle(
            cornerRadius: min(height / 2, LegendNextRadius.compact),
            style: .continuous
        )
        .fill(LegendNextColor.fillSecondary)
        .frame(width: width, height: height)
        .legendNextShimmer()
        .accessibilityHidden(true)
    }
}

struct LegendNextSkeletonCard: View {
    var body: some View {
        LegendNextSurface(style: .elevated) {
            HStack(spacing: LegendNextSpacing.md) {
                Circle()
                    .fill(LegendNextColor.fillSecondary)
                    .frame(
                        width: LegendNextSize.avatarMedium,
                        height: LegendNextSize.avatarMedium
                    )
                    .legendNextShimmer()

                VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                    LegendNextSkeletonLine(width: 150, height: 14)
                    LegendNextSkeletonLine(width: 210, height: 11)
                    LegendNextSkeletonLine(width: 112, height: 11)
                }

                Spacer(minLength: 0)
            }
        }
        .accessibilityLabel("Loading content")
    }
}
