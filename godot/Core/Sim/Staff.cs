namespace Stations.Core.Sim;

public static class Staff
{
    private static readonly string[] First =
    {
        "Anna", "Ben", "Clara", "Deniz", "Elif", "Felix", "Greta", "Hannes", "Ida", "Jonas", "Katrin", "Lukas",
        "Mia", "Nico", "Olga", "Paul", "Rosa", "Sami", "Tina", "Uwe", "Vera", "Willi", "Yusuf", "Zoe", "Marek",
        "Leonie", "Tobias", "Sabine", "Ahmet", "Jana", "Frank", "Ines", "Kai", "Nadine", "Ole", "Petra",
    };

    private static readonly string[] Last =
    {
        "Müller", "Schmidt", "Schneider", "Fischer", "Weber", "Meyer", "Wagner", "Becker", "Schulz", "Hoffmann",
        "Koch", "Richter", "Klein", "Wolf", "Neumann", "Schwarz", "Braun", "Krüger", "Hartmann", "Lange", "Yılmaz",
        "Kowalski", "Werner", "Krause", "Lehmann", "Köhler", "Peters", "Jansen", "Petersen", "Arslan", "Nowak",
    };

    public static string RandomName(Random rng) => $"{First[rng.Next(First.Length)]} {Last[rng.Next(Last.Length)]}";
}
