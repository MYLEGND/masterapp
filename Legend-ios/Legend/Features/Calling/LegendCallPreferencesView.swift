import SwiftUI

struct LegendCallPreferencesView: View {
    @ObservedObject var store: LegendCallStore
    var openProfilePhoto: (() -> Void)? = nil
    @Environment(\.dismiss) private var dismiss
    @State private var draft: LegendCallPreferences?
    var body: some View {
        NavigationStack {
            Form {
                if let error = store.preferencesError { Text(LegendLocalized(error)).foregroundStyle(LegendNextColor.danger) }
                if let current = draft {
                    Picker(LegendLocalized("Incoming ringtone"), selection: Binding(get: { current.ringtoneId }, set: { draft?.ringtoneId = $0 })) {
                        ForEach(store.ringtones) { choice in Text(LegendLocalized(choice.label)).tag(choice.id) }
                    }
                    Picker(LegendLocalized("Call background"), selection: Binding(get: { current.wallpaperMode }, set: { draft?.wallpaperMode = $0 })) {
                        ForEach(store.wallpapers) { choice in Text(LegendLocalized(choice.label)).tag(choice.id) }
                    }
                    if let openProfilePhoto {
                        Button(LegendLocalized("Edit account profile photo")) { dismiss(); openProfilePhoto() }
                    }
                    Button(LegendLocalized("Save")) { store.savePreferences(current) }
                        .disabled(store.preferencesBusy || current == store.preferences)
                } else if store.preferencesBusy { ProgressView() }
                else { Button(LegendLocalized("Retry")) { store.loadPreferences() } }
            }
            .navigationTitle(LegendLocalized("Calling profile"))
            .toolbar { ToolbarItem(placement: .confirmationAction) { Button(LegendLocalized("Done")) { dismiss() } } }
            .onChange(of: store.preferences, initial: true) { _, value in draft = value }
            .task { store.loadPreferences() }
        }
    }
}
