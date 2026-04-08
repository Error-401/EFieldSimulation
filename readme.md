<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0-windows10.0.22621.0</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <WindowsSdkPackageVersion>10.0.22621.53</WindowsSdkPackageVersion>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="AssimpNet" Version="4.1.0" />
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.0" />
    <PackageReference Include="HDF.PInvoke.1.10" Version="1.10.612" />
    <PackageReference Include="HDF5-CSharp" Version="1.19.1" />
    <PackageReference Include="HelixToolkit.Wpf" Version="3.1.2" />
    <PackageReference Include="ILGPU" Version="1.5.3" />
    <PackageReference Include="ILGPU.Algorithms" Version="1.5.3" />
    <PackageReference Include="System.Numerics.Vectors" Version="4.5.0" />
    <PackageReference Include="WriteableBitmapEx" Version="1.6.11" />
    <PackageReference Include="Tetgen.SilverHorn" Version="1.5.2" />
    <PackageReference Include="OxyPlot.Wpf" Version="2.1.4" />
  </ItemGroup>

  <ItemGroup>
    <Page Include="App.xaml" />
  </ItemGroup>

  <ItemGroup>
    <Folder Include="Utilities\" />
  </ItemGroup>
</Project>


The whole goal is to have a simulation running and either do containment analysis, or move a mobile mesh and do pathline calculations and make sure the the system to extract electrons can actually get them out.
