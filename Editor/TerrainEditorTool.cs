using System;
using Editor;
using System.Linq;
using Sandbox;
using RedSnail.RoadTool;

namespace RedSnail.RoadTool.Editor;

/// <summary>
/// Editor tool used to deform the terrain storage to align with a selected road spline.
/// Provides controls for falloff radius, sampling precision, and height offsets.
/// </summary>
[Title( "Terrain" )]
[Icon( "landscape" )]
[Group( "1" )]
[Order( 0 )]
public class TerrainEditorTool : EditorTool
{
    public override Widget CreateToolSidebar()
    {
        ToolSidebarWidget sidebar = new ToolSidebarWidget();
        sidebar.AddTitle( "Terrain", "landscape" );

        var selection = SceneEditorSession.Active.Selection.FirstOrDefault();
        Component targetComponent = null;

        if ( selection is RoadComponent r ) targetComponent = r;
        else if ( selection is RoadIntersectionComponent i ) targetComponent = i;
        else if ( selection is GameObject go )
        {
            targetComponent = go.Components.Get<RoadComponent>() ?? (Component)go.Components.Get<RoadIntersectionComponent>();
        }

        Layout group = sidebar.AddGroup( "Properties" );

        if ( targetComponent.IsValid() )
        {
            var serialized = targetComponent.GetSerialized();
            // StepPrecision isn't used for intersections, so we filter it out if needed
            var targetProperties = targetComponent is RoadComponent
                ? new[] { "TerrainFalloffRadius", "TerrainStepPrecision", "TerrainHeightOffset" }
                : new[] { "TerrainFalloffRadius", "TerrainHeightOffset" };

            foreach ( var propName in targetProperties )
            {
                var prop = serialized.GetProperty( propName );
                if ( prop == null ) continue;

                // Create a vertical container to stack the label and the slider
                var propLayout = group.AddColumn();
                propLayout.Spacing = 2;
                propLayout.Margin = new Sandbox.UI.Margin( 0, 4, 0, 4 );

                // Use ControlSheet.CreateLabel to maintain style and functionality (drag & drop)
                propLayout.Add( ControlSheet.CreateLabel( prop ) );

                // Generate the control widget (which will be a Slider thanks to the [Range] attribute)
                // By placing it in a column, it will take the full available width
                propLayout.Add( ControlWidget.Create( prop ) );
            }
        }
        else
        {
            group.Add( new Label( "Select a route to edit" ) );
        }

        // Add button outside the group (just below)
        var buttonLayout = BuildControlButtons();
        sidebar.Layout.Add( buttonLayout );

        // Flexible space to push everything to the top
        sidebar.Layout.AddStretchCell();

        return sidebar;
    }

    private Layout BuildControlButtons()
    {
        var row = Layout.Row();
        row.Margin = new Sandbox.UI.Margin( 0, 8, 0, 0 ); // Small space above the button
        row.Add( new Button( "Apply to the Ground", "landscape" ) { Clicked = AlignTerrainToRoad } );
        return row;
    }


    /// <summary>
    /// This method applies to the selected RoadComponent.
    /// </summary>
    public static void AlignTerrainToRoad()
    {
        var selection = SceneEditorSession.Active.Selection.FirstOrDefault();
        RoadComponent road = null;
        RoadIntersectionComponent intersection = null;

        if ( selection is RoadComponent r ) road = r;
        else if ( selection is RoadIntersectionComponent i ) intersection = i;
        else if ( selection is GameObject go )
        {
            road = go.Components.Get<RoadComponent>();
            intersection = go.Components.Get<RoadIntersectionComponent>();
        }

        if ( road == null && intersection == null )
        {
            Log.Warning( "RoadTool: Please select a Road or Intersection to use this tool." );
            return;
        }

        var terrain = SceneEditorSession.Active.Scene.GetAllComponents<Terrain>().FirstOrDefault();
        if ( terrain == null ) return;

        // 1. Capture state BEFORE
        var storage = terrain.Storage;
        var oldHeightMap = new ushort[storage.HeightMap.Length];
        Array.Copy( storage.HeightMap, oldHeightMap, oldHeightMap.Length );

        // 2. Execute modification based on type
        if ( road.IsValid() ) road.AdaptTerrainToRoad();
        else if ( intersection.IsValid() ) intersection.AdaptTerrainToIntersection();

        // 3. Capture state AFTER
        var newHeightMap = new ushort[storage.HeightMap.Length];
        Array.Copy( storage.HeightMap, newHeightMap, newHeightMap.Length );

        // 4. Register in editor history
        var targetTerrain = terrain;
        var targetStorage = storage;

        SceneEditorSession.Active.AddUndo( "Align Terrain to Road",
            undo: () =>
            {
                if ( !targetTerrain.IsValid() ) return;
                targetStorage.HeightMap = oldHeightMap;
                targetStorage.StateHasChanged();
                targetTerrain.Create();
                targetTerrain.SyncGPUTexture();
            },
            redo: () =>
            {
                if ( !targetTerrain.IsValid() ) return;
                targetStorage.HeightMap = newHeightMap;
                targetStorage.StateHasChanged();
                targetTerrain.Create();
                targetTerrain.SyncGPUTexture();
            } );
    }
}
