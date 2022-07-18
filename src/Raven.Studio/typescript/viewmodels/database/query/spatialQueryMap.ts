import viewModelBase = require("viewmodels/viewModelBase");
import spatialMarkersLayerModel = require("models/database/query/spatialMarkersLayerModel");
import document = require("models/database/documents/document");
import documentMetadata = require("models/database/documents/documentMetadata");
import { Control, IconOptions, MarkerClusterGroup, Map, TileLayer } from "leaflet";
import genUtils = require("common/generalUtils");
import spatialCircleModel = require("models/database/query/spatialCircleModel");
import spatialPolygonModel = require("models/database/query/spatialPolygonModel");
import { highlight, languages } from "prismjs";

import L = require("leaflet");
import "leaflet.markercluster";

const markerIcon = require("Content/img/leaflet/marker-icon.svg");

class spatialQueryMap extends viewModelBase {

    view = require("views/database/query/spatialQueryMap.html");
    
    private map: Map;
    
    markersLayers = ko.observableArray<spatialMarkersLayerModel>([]);
    circlesLayer = ko.observableArray<spatialCircleModel>([]);
    polygonsLayer = ko.observableArray<spatialPolygonModel>([]);

    constructor(markers: Array<spatialMarkersLayerModel>, circles: spatialCircleModel[], polygons: spatialPolygonModel[]) {
        super();
        this.markersLayers(markers);
        this.circlesLayer(circles);
        this.polygonsLayer(polygons);
    }

    compositionComplete(): void {
        super.compositionComplete();
        this.createMap();
    }
    
    private createMap(): void {
        const osmMap = this.getStreetMapTileLayer();
        const otmMap = this.getTopographyMapTileLayer();
        const baseLayers = {
            "Streets Map": osmMap,
            "Topography Map": otmMap
        };

        const generatePopupHtml = (doc: document) => {
            const docDto = doc.toDto(true);
            const metaDto = docDto["@metadata"];
            documentMetadata.filterMetadata(metaDto);

            let text = JSON.stringify(docDto, null, 4);
            text = highlight(text, languages.javascript, "js");

            return `<div>
                          <h4>Document: ${genUtils.escapeHtml(doc.getId())}</h4>
                          <pre>${text}</pre>
                    </div>`;
        }
        
        const dataLayers: Control.LayersObject = {};
        const markersGroups: MarkerClusterGroup[] = [];

        const ravenMarker = L.icon({
            iconUrl: markerIcon,
            iconSize: [35, 26],
            iconAnchor: [17, 22],
            popupAnchor: [5, -22],
            tooltipAnchor: [0, -17]
        } as IconOptions);

        this.markersLayers().forEach(markersLayer => {
            const markers = markersLayer.geoPoints().map(point => {
                return L.marker([point.Latitude, point.Longitude], { icon: ravenMarker })
                    .bindTooltip(point.PopupContent.getId(), { direction: 'top' })
                    .bindPopup(() => generatePopupHtml(point.PopupContent), { "className" : "custom-popup", "maxWidth": 600, "maxHeight": 400 } );
            });

            const markersGroup = L.markerClusterGroup();
            markersGroup.addLayers(markers);
            const numberOfMarkersInLayer = markers.length;
            
            const controlLayerText = `<span>Point fields: <span class="number-of-markers pull-right padding padding-xs">${numberOfMarkersInLayer}</span></span>
                                      <div class='margin-left'>
                                          <span>${genUtils.escapeHtml(markersLayer.latitudeProperty)}</span><br>
                                          <span>${genUtils.escapeHtml(markersLayer.longitudeProperty)}</span>
                                      </div>`;

            // A markersGroup layer is per spatial point from query
            (dataLayers as any)[controlLayerText] = markersGroup;
            markersGroups.push(markersGroup);
        })
        
        const polyArray = this.polygonsLayer().map((poly, index) => L.polygon(poly.vertices,
            { color: spatialPolygonModel.colors[index % spatialPolygonModel.colors.length], fillOpacity: 0.2 }));

        const circleArray = this.circlesLayer().map((circle, index) => L.circle([circle.latitude, circle.longitude],
            { color: spatialCircleModel.colors[index % spatialCircleModel.colors.length], fillOpacity: 0.2, radius: circle.radius }));
        
        const polyLayer = L.layerGroup(polyArray);
        const circleLayer = L.layerGroup(circleArray);
        
        if (polyArray.length) {
            (dataLayers as any)["Polygons"] = polyLayer;
        }
        
        if (circleArray.length) {
            (dataLayers as any)["Circles"] = circleLayer;
        }

        // Must init L.map w/ some options, otherwise method Circle.getBounds() will fail
        // The map.fitBounds method that is called later overrides these options
        this.map = L.map("mapid", {center:[0, 0], zoom: 1, preferCanvas: true});

        osmMap.addTo(this.map);
        L.control.layers(baseLayers, dataLayers).addTo(this.map);
        
        markersGroups.forEach(group => this.map.addLayer(group));
        this.map.addLayer(polyLayer);
        this.map.addLayer(circleLayer);

        const mapBounds = L.latLngBounds([]);
        markersGroups.forEach(group => mapBounds.extend(group.getBounds()));
       
        polyArray.forEach(poly => mapBounds.extend(poly.getBounds()));
        circleArray.forEach(circ => mapBounds.extend(circ.getBounds()));

        this.map.fitBounds(mapBounds, {padding: [50, 50]});
    }
    
    private getStreetMapTileLayer(): TileLayer {
        const osmLink = `<a href="http://openstreetmap.org">OpenStreetMap</a>`;
        const osmUrl = 'http://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png';
        const osmAttrib = `&copy; ${osmLink} Contributors`;
        return L.tileLayer(osmUrl, {attribution: osmAttrib});
    }
    
    private getTopographyMapTileLayer(): TileLayer {
        const otmLink = `<a href="http://opentopomap.org/">OpenTopoMap</a>`;
        const otmUrl = `http://{s}.tile.opentopomap.org/{z}/{x}/{y}.png`;
        const otmAttrib = `&copy; ${otmLink} Contributors`;
        return L.tileLayer(otmUrl, {attribution: otmAttrib});
    } 
    
    onResize(): void {
        this.map.invalidateSize();
    }
}

export = spatialQueryMap;
