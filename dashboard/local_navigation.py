"""Deterministic floor/obstacle policy. No model-generated motor commands."""
import numpy as np

# Polygons converge toward the center footprint at the bottom of the image.
PATHS = {
    'left': [(0.30, .96), (.70, .96), (.40, .52), (.08, .52)],
    'forward': [(.30, .96), (.70, .96), (.64, .48), (.36, .48)],
    'right': [(.30, .96), (.70, .96), (.92, .52), (.60, .52)],
}


def evaluate(floor, objects):
    import cv2
    floor = np.asarray(floor, dtype=bool)
    if floor.ndim != 2 or min(floor.shape) < 16:
        raise ValueError('Invalid floor mask')
    height, width = floor.shape
    blocked = np.zeros_like(floor)
    nearby = []
    for obj in objects:
        x1,y1,x2,y2 = obj['box']
        if y2 < .5 or obj['confidence'] < .35:
            continue
        # Object boxes are deliberately inflated; unlabeled objects are covered
        # by the independent floor segmentation, rather than treated as free space.
        xa,xb=max(0,int((x1-.035)*width)),min(width,int((x2+.035)*width)+1)
        ya,yb=max(0,int((y1-.025)*height)),min(height,int((y2+.025)*height)+1)
        blocked[ya:yb,xa:xb] = True
        nearby.append(obj['label'])
    usable = floor & ~blocked
    scores = {}
    for direction, polygon in PATHS.items():
        region=np.zeros((height,width),np.uint8)
        cv2.fillPoly(region,[np.array([(int(x*width),int(y*height)) for x,y in polygon],np.int32)],1)
        region=region.astype(bool)
        coverage=float(usable[region].mean())
        obstacle=float(blocked[region].mean())
        rows=[]
        for a,b in ((.5,.65),(.65,.8),(.8,.96)):
            band=region[int(a*height):int(b*height)]
            pixels=usable[int(a*height):int(b*height)]
            rows.append(float(pixels[band].mean()) if band.any() else 0)
        scores[direction]=dict(floor=round(coverage,3),obstacle=round(obstacle,3),
            clear=coverage>=.80 and min(rows)>=.65 and obstacle<.015)
    # The visible near-center footprint must be free for any commanded motion.
    anchor=usable[int(.8*height):int(.96*height),int(.32*width):int(.68*width)]
    if float(anchor.mean()) < .85:
        action='wait';reason='Near floor is obstructed or not confidently identified'
    elif scores['forward']['clear']:
        action='forward';reason='Visible floor ahead is clear'
    else:
        options=[d for d in ('left','right') if scores[d]['clear']]
        if options:
            action=max(options,key=lambda d:scores[d]['floor'])
            reason='Center path blocked; visible floor is clearer to the '+action
        else:
            action='wait';reason='No visible clear route'
    if nearby:reason+=' · nearby: '+', '.join(dict.fromkeys(nearby))
    return dict(action=action,reason=reason,sectors=scores),usable
