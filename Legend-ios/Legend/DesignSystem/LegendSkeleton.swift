import SwiftUI

/// Content-shaped placeholders used instead of a spinner.
///
/// A spinner tells someone to wait. A skeleton tells them what is arriving and keeps
/// the layout stable, so the screen never visibly "pops" when real content lands.
/// Legend uses these everywhere a surface is waiting on its first payload.
struct LegendSkeletonShape: View {
    var cornerRadius: CGFloat = 8

    @State private var isAnimating = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
            .fill(LegendNextColor.surfaceInset)
            .overlay {
                if !reduceMotion {
                    RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
                        .fill(
                            LinearGradient(
                                colors: [
                                    Color.white.opacity(0),
                                    Color.white.opacity(0.35),
                                    Color.white.opacity(0)
                                ],
                                startPoint: .leading,
                                endPoint: .trailing))
                        .offset(x: isAnimating ? 220 : -220)
                }
            }
            .clipShape(RoundedRectangle(cornerRadius: cornerRadius, style: .continuous))
            .onAppear {
                guard !reduceMotion else { return }
                withAnimation(.linear(duration: 1.1).repeatForever(autoreverses: false)) {
                    isAnimating = true
                }
            }
    }
}

/// A row of avatar rings, matching the story rail.
struct LegendStoryRailSkeleton: View {
    var count = 6

    var body: some View {
        HStack(spacing: LegendNextSpacing.md) {
            ForEach(0..<count, id: \.self) { _ in
                VStack(spacing: LegendNextSpacing.xs) {
                    LegendSkeletonShape(cornerRadius: 29)
                        .frame(width: 58, height: 58)
                    LegendSkeletonShape(cornerRadius: 4)
                        .frame(width: 44, height: 8)
                }
            }
        }
        .padding(.horizontal, 2)
        .accessibilityHidden(true)
    }
}

/// A feed post placeholder: author line, media block, action row.
struct LegendFeedPostSkeleton: View {
    var body: some View {
        VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
            HStack(spacing: LegendNextSpacing.xs) {
                LegendSkeletonShape(cornerRadius: 19)
                    .frame(width: 38, height: 38)
                VStack(alignment: .leading, spacing: 5) {
                    LegendSkeletonShape(cornerRadius: 4).frame(width: 130, height: 10)
                    LegendSkeletonShape(cornerRadius: 4).frame(width: 84, height: 8)
                }
                Spacer()
            }

            LegendSkeletonShape(cornerRadius: LegendNextRadius.card)
                .frame(height: 240)

            HStack(spacing: LegendNextSpacing.sm) {
                ForEach(0..<3, id: \.self) { _ in
                    LegendSkeletonShape(cornerRadius: 6).frame(width: 52, height: 12)
                }
                Spacer()
            }
        }
        .accessibilityHidden(true)
    }
}

/// A stack of list rows, for directories and CRM lists.
struct LegendListSkeleton: View {
    var rows = 8
    var showsAvatar = true

    var body: some View {
        VStack(spacing: LegendNextSpacing.sm) {
            ForEach(0..<rows, id: \.self) { _ in
                HStack(spacing: LegendNextSpacing.xs) {
                    if showsAvatar {
                        LegendSkeletonShape(cornerRadius: 26)
                            .frame(width: 52, height: 52)
                    }
                    VStack(alignment: .leading, spacing: 6) {
                        LegendSkeletonShape(cornerRadius: 4).frame(width: 150, height: 11)
                        LegendSkeletonShape(cornerRadius: 4).frame(maxWidth: .infinity)
                            .frame(height: 9)
                        LegendSkeletonShape(cornerRadius: 4).frame(width: 110, height: 9)
                    }
                    Spacer(minLength: 0)
                }
            }
        }
        .accessibilityHidden(true)
    }
}

/// Stat tiles plus card blocks, matching the Home layout.
struct LegendHomeSkeleton: View {
    var body: some View {
        VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
            VStack(alignment: .leading, spacing: 8) {
                LegendSkeletonShape(cornerRadius: 5).frame(width: 190, height: 14)
                LegendSkeletonShape(cornerRadius: 5).frame(width: 250, height: 10)
            }

            LegendSkeletonShape(cornerRadius: LegendNextRadius.card)
                .frame(height: 128)

            HStack(spacing: LegendNextSpacing.sm) {
                ForEach(0..<3, id: \.self) { _ in
                    LegendSkeletonShape(cornerRadius: LegendNextRadius.control)
                        .frame(height: 72)
                }
            }

            LegendStoryRailSkeleton()
            LegendFeedPostSkeleton()
        }
        .accessibilityHidden(true)
    }
}

/// The whole-screen placeholder used before the shell knows who the user is.
/// Deliberately brand-led rather than a gear: it reads as the app opening.
struct LegendLaunchSkeleton: View {
    var body: some View {
        VStack(spacing: LegendNextSpacing.lg) {
            Spacer()
            LegendBrandLogo(maximumWidth: 168)
                .accessibilityLabel("Legend")
            Spacer()
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color.white.ignoresSafeArea())
    }
}

/// A skeleton that fills the screen the same way the eventual content will.
struct LegendScreenSkeleton<Content: View>: View {
    let accessibilityMessage: String
    @ViewBuilder let content: () -> Content

    var body: some View {
        ScrollView {
            content()
                .padding(.horizontal, LegendNextSpacing.sm)
                .padding(.top, LegendNextSpacing.sm)
        }
        .scrollDisabled(true)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(accessibilityMessage)
    }
}
