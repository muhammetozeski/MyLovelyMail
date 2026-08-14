using System.Runtime.CompilerServices;

/// <summary>
/// Holds navigation data for the source generator. 
/// The C# compiler strictly prevents duplicate tuple element names (CS8306).
/// </summary>
static class NavigationData
{
    static ITuple Pages => (
        Home: "🏠",
        Logina: "🔑",
        Profilee: "👤",
        Tab1: "1️⃣",
        Settings: "/images/settings.png"
    );
}